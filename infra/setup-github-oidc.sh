#!/usr/bin/env bash
# One-time: let GitHub Actions deploy to Azure WITHOUT storing any Azure password or key in GitHub.
# Creates an Entra app + service principal, trusts GitHub's OIDC tokens for this repo's "production"
# environment only, and grants it rights on ONE resource group.
#
# Usage: ./infra/setup-github-oidc.sh <subscription-id> <resource-group> [github-owner/repo]
set -euo pipefail

SUBSCRIPTION_ID="${1:?subscription id}"
RESOURCE_GROUP="${2:?resource group}"
REPO="${3:-vipul2902/AI-SupportOps}"
APP_NAME="github-deploy-aisupportops"

az account set --subscription "$SUBSCRIPTION_ID"
TENANT_ID=$(az account show --query tenantId -o tsv)
RG_ID=$(az group show -n "$RESOURCE_GROUP" --query id -o tsv)

APP_ID=$(az ad app create --display-name "$APP_NAME" --query appId -o tsv)
az ad sp create --id "$APP_ID" -o none 2>/dev/null || true

# Trust only workflow runs that target the "production" GitHub environment of this repository.
az ad app federated-credential create --id "$APP_ID" --parameters "{
  \"name\": \"github-production\",
  \"issuer\": \"https://token.actions.githubusercontent.com\",
  \"subject\": \"repo:${REPO}:environment:production\",
  \"audiences\": [\"api://AzureADTokenExchange\"]
}" -o none

# Least privilege: Contributor on the single resource group (updates apps, starts the migration job).
az role assignment create --assignee "$APP_ID" --role Contributor --scope "$RG_ID" -o none

cat <<EOF

Done. In GitHub: Settings > Environments > production > Variables, add:
  AZURE_CLIENT_ID       = $APP_ID
  AZURE_TENANT_ID       = $TENANT_ID
  AZURE_SUBSCRIPTION_ID = $SUBSCRIPTION_ID
  AZURE_RESOURCE_GROUP  = $RESOURCE_GROUP
(These are identifiers, not secrets.) Optionally add required reviewers to the environment.
EOF
