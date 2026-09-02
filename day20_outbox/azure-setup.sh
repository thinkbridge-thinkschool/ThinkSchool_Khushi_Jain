#!/usr/bin/env bash
set -euo pipefail

group=rg-day20-outbox
location=eastus
queue=quote-outbox

suffix=$(az account show --query id -o tsv | tr -d '-' | cut -c1-12)
namespace="sbday20$suffix"

echo "Namespace $namespace in $group ($location)." >&2

az group create --name "$group" --location "$location" -o none

# Basic tier, not Standard. Only a queue is needed here, and Basic bills per
# operation with no hourly charge, so an idle namespace costs nothing.
az servicebus namespace create --name "$namespace" --resource-group "$group" \
    --location "$location" --sku Basic -o none

az servicebus queue create --name "$queue" --namespace-name "$namespace" \
    --resource-group "$group" --max-delivery-count 5 -o none

echo "Queue $queue ready." >&2

az servicebus namespace authorization-rule keys list --resource-group "$group" \
    --namespace-name "$namespace" --name RootManageSharedAccessKey \
    --query primaryConnectionString -o tsv
