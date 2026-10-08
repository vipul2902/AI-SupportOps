// AI-SupportOps on Azure. Deploy to a resource group:
//   az deployment group create -g <rg> -f infra/main.bicep -p infra/main.parameters.json -p postgresAdminPassword=... jwtSigningKey=...
//
// Shape: Azure Container Apps (web = nginx + SPA, public; api = internal) in a VNet, a Container Apps
// Job for migrations, PostgreSQL Flexible Server (pgvector), Azure Managed Redis, Blob Storage for
// documents, Key Vault for secrets, Application Insights for telemetry. One user-assigned managed
// identity is used for Key Vault and Blob access: no keys or passwords in app configuration.

targetScope = 'resourceGroup'

@description('Short prefix for resource names.')
@maxLength(8)
param namePrefix string = 'aiso'

param location string = resourceGroup().location

@description('API container image, e.g. ghcr.io/vipul2902/aisupportops-api:sha-abc1234')
param apiImage string

@description('Web (nginx + SPA) container image, e.g. ghcr.io/vipul2902/aisupportops-web:sha-abc1234')
param webImage string

param postgresAdminLogin string = 'aisoadmin'

@secure()
param postgresAdminPassword string

@secure()
@minLength(32)
param jwtSigningKey string

@allowed(['OpenAI', 'Fake'])
param aiProvider string = 'OpenAI'

@secure()
@description('Required when aiProvider is OpenAI.')
param openAiApiKey string = ''

param openAiChatModel string = 'gpt-4o-mini'

@description('PostgreSQL major version (must be offered by Flexible Server in the chosen region).')
param postgresVersion string = '17'

var suffix = uniqueString(resourceGroup().id)
var apiAppName = '${namePrefix}-api'
var webAppName = '${namePrefix}-web'
var infraSubnetCidr = '10.40.0.0/23'
var useOpenAi = aiProvider == 'OpenAI'

// Built-in role definition ids
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'
var storageBlobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

// ---------------------------------------------------------------------------------------------
// Observability: Log Analytics (Container Apps logs) + workspace-based Application Insights.
// ---------------------------------------------------------------------------------------------
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs-${suffix}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-ai-${suffix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------------------------------------------------------------------------------------------
// Identity: one user-assigned managed identity for the apps and the migration job.
// ---------------------------------------------------------------------------------------------
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-id-${suffix}'
  location: location
}

// ---------------------------------------------------------------------------------------------
// Network: a known infrastructure subnet, so the API can trust forwarded headers from exactly it.
// ---------------------------------------------------------------------------------------------
resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${namePrefix}-vnet-${suffix}'
  location: location
  properties: {
    addressSpace: { addressPrefixes: ['10.40.0.0/16'] }
    subnets: [
      {
        name: 'aca-infrastructure'
        properties: {
          addressPrefix: infraSubnetCidr
          delegations: [{ name: 'aca', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
    ]
  }
}

// ---------------------------------------------------------------------------------------------
// Secrets: Key Vault with RBAC; apps read secrets through the managed identity.
// ---------------------------------------------------------------------------------------------
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${namePrefix}-kv-${suffix}'
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
  }
}

resource vaultReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, identity.id, keyVaultSecretsUser)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------------------------------------
// PostgreSQL Flexible Server with pgvector allow-listed (migrations run CREATE EXTENSION vector).
// ---------------------------------------------------------------------------------------------
resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: '${namePrefix}-pg-${suffix}'
  location: location
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: postgresVersion
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminPassword
    storage: { storageSizeGB: 32, autoGrow: 'Enabled' }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
  }
}

resource pgVectorAllowList 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: postgres
  name: 'azure.extensions'
  properties: { value: 'VECTOR', source: 'user-override' }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'aisupportops'
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}

// Public endpoint restricted to Azure services (TLS required by the connection string).
// Stronger: VNet integration / private endpoint (see docs/deployment.md, "Hardening next").
resource pgAllowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

// ---------------------------------------------------------------------------------------------
// Redis: Azure Managed Redis (TLS on port 10000). Holds only disposable data (cache, rate counters).
// ---------------------------------------------------------------------------------------------
resource redis 'Microsoft.Cache/redisEnterprise@2025-04-01' = {
  name: '${namePrefix}-redis-${suffix}'
  location: location
  sku: { name: 'Balanced_B0' }
  properties: { minimumTlsVersion: '1.2' }
}

resource redisDb 'Microsoft.Cache/redisEnterprise/databases@2025-04-01' = {
  parent: redis
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    port: 10000
    clusteringPolicy: 'OSSCluster'
    evictionPolicy: 'AllKeysLRU'
    accessKeysAuthentication: 'Enabled'
  }
}

// ---------------------------------------------------------------------------------------------
// Blob Storage for documents: shared across replicas, durable, private, no shared keys.
// ---------------------------------------------------------------------------------------------
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: toLower('${namePrefix}st${suffix}')
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false // identity-only access
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource documentsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'documents'
  properties: { publicAccess: 'None' }
}

resource blobWriter 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, storageBlobDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------------------------------------
// Secret values (built here so they never appear in app settings).
// ---------------------------------------------------------------------------------------------
resource secretPostgres 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'postgres-connection'
  properties: {
    value: 'Host=${postgres.properties.fullyQualifiedDomainName};Port=5432;Database=${database.name};Username=${postgresAdminLogin};Password=${postgresAdminPassword};SSL Mode=Require'
  }
}

resource secretRedis 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'redis-connection'
  properties: {
    value: '${redis.properties.hostName}:10000,password=${redisDb.listKeys().primaryKey},ssl=True,abortConnect=False'
  }
}

resource secretJwt 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'jwt-signing-key'
  properties: { value: jwtSigningKey }
}

resource secretOpenAi 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (useOpenAi) {
  parent: vault
  name: 'openai-api-key'
  properties: { value: openAiApiKey }
}

// ---------------------------------------------------------------------------------------------
// Container Apps environment (workload profiles, consumption) in the VNet.
// ---------------------------------------------------------------------------------------------
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-env-${suffix}'
  location: location
  properties: {
    vnetConfiguration: { infrastructureSubnetId: vnet.properties.subnets[0].id }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

var kvSecrets = concat([
  { name: 'postgres-connection', keyVaultUrl: secretPostgres.properties.secretUri, identity: identity.id }
  { name: 'redis-connection', keyVaultUrl: secretRedis.properties.secretUri, identity: identity.id }
  { name: 'jwt-signing-key', keyVaultUrl: secretJwt.properties.secretUri, identity: identity.id }
], useOpenAi ? [
  { name: 'openai-api-key', keyVaultUrl: secretOpenAi!.properties.secretUri, identity: identity.id }
] : [])

var apiEnv = concat([
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'ConnectionStrings__Postgres', secretRef: 'postgres-connection' }
  { name: 'ConnectionStrings__Redis', secretRef: 'redis-connection' }
  { name: 'Auth__SigningKey', secretRef: 'jwt-signing-key' }
  { name: 'Ai__Provider', value: aiProvider }
  { name: 'Ai__OpenAI__ChatModel', value: openAiChatModel }
  { name: 'Storage__Provider', value: 'AzureBlob' }
  { name: 'Storage__AzureBlob__ServiceUri', value: storage.properties.primaryEndpoints.blob }
  { name: 'Storage__AzureBlob__ContainerName', value: documentsContainer.name }
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId } // DefaultAzureCredential -> this identity
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
  // Requests reach the API via ingress -> nginx -> ingress, all inside this subnet.
  { name: 'ForwardedHeaders__KnownNetworks__0', value: infraSubnetCidr }
  { name: 'ForwardedHeaders__ForwardLimit', value: '2' }
], useOpenAi ? [
  { name: 'Ai__OpenAI__ApiKey', secretRef: 'openai-api-key' }
] : [])

var userIdentity = {
  type: 'UserAssigned'
  userAssignedIdentities: { '${identity.id}': {} }
}

// One-shot migration job, run by the deploy workflow before new app revisions take traffic.
resource migrateJob 'Microsoft.App/jobs@2024-03-01' = {
  name: '${namePrefix}-migrate'
  location: location
  identity: userIdentity
  dependsOn: [vaultReader, pgVectorAllowList, pgAllowAzure]
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
      replicaRetryLimit: 1
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      secrets: kvSecrets
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: apiImage
          args: ['--migrate-only']
          env: apiEnv
          resources: { cpu: json('0.5'), memory: '1Gi' }
        }
      ]
    }
  }
}

resource apiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: apiAppName
  location: location
  identity: userIdentity
  dependsOn: [vaultReader, blobWriter, pgVectorAllowList, pgAllowAzure]
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      secrets: kvSecrets
      ingress: {
        external: false // reachable only inside the environment (from nginx)
        targetPort: 8080
        transport: 'http'
        allowInsecure: true // internal hop from nginx; TLS terminates at the public ingress
      }
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          env: apiEnv
          resources: { cpu: json('1.0'), memory: '2Gi' }
          probes: [
            { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 15 }
            { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 }, periodSeconds: 10 }
          ]
        }
      ]
      // minReplicas 1: the document-ingestion worker runs inside the API and must not scale to zero.
      scale: {
        minReplicas: 1
        maxReplicas: 3
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '50' } } }]
      }
    }
  }
}

resource webApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: webAppName
  location: location
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false // HTTPS only; HTTP redirects
      }
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage
          env: [{ name: 'API_UPSTREAM', value: apiAppName }] // Container Apps internal service name
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          probes: [{ type: 'Liveness', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 15 }]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '100' } } }]
      }
    }
  }
}

output webUrl string = 'https://${webApp.properties.configuration.ingress.fqdn}'
output apiAppName string = apiApp.name
output webAppName string = webApp.name
output migrateJobName string = migrateJob.name
output keyVaultName string = vault.name
