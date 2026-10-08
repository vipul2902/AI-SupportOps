# Integrations

## Slack

Connect Slack from the Integrations page to receive ticket notifications in a channel. Each workspace can connect one Slack team.

## Webhooks

Webhooks send an HTTP POST to your endpoint when a ticket is created, updated, or closed. Every webhook request is signed with an HMAC SHA-256 signature in the X-Signature header so you can verify it came from us. Failed webhook deliveries are retried with exponential backoff for up to 24 hours.

## API rate limits

The public API allows 600 requests per minute per workspace. Requests above the limit receive HTTP 429 with a Retry-After header.
