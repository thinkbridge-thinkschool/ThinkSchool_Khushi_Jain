#!/usr/bin/env bash
set -euo pipefail

group=rg-day19-servicebus
location=eastus
topic=quote-events

suffix=$(az account show --query id -o tsv | tr -d '-' | cut -c1-12)
namespace="sbday19$suffix"

echo "Namespace $namespace in $group ($location)." >&2

az group create --name "$group" --location "$location" -o none

az servicebus namespace create --name "$namespace" --resource-group "$group" \
    --location "$location" --sku Standard -o none

az servicebus topic create --name "$topic" --namespace-name "$namespace" \
    --resource-group "$group" -o none

az servicebus topic subscription create --name audit --topic-name "$topic" \
    --namespace-name "$namespace" --resource-group "$group" --max-delivery-count 2 -o none

az servicebus topic subscription create --name moderation --topic-name "$topic" \
    --namespace-name "$namespace" --resource-group "$group" --max-delivery-count 3 -o none

az servicebus topic subscription rule create --name created-only --subscription-name moderation \
    --topic-name "$topic" --namespace-name "$namespace" --resource-group "$group" \
    --filter-type SqlFilter --filter-sql-expression "eventType = 'QuoteCreated'" -o none

if az servicebus topic subscription rule show --name '$Default' --subscription-name moderation \
    --topic-name "$topic" --namespace-name "$namespace" --resource-group "$group" -o none 2>/dev/null
then
    az servicebus topic subscription rule delete --name '$Default' --subscription-name moderation \
        --topic-name "$topic" --namespace-name "$namespace" --resource-group "$group" -o none
fi

echo "Topic $topic ready: audit takes everything, moderation takes QuoteCreated only." >&2

az servicebus namespace authorization-rule keys list --resource-group "$group" \
    --namespace-name "$namespace" --name RootManageSharedAccessKey \
    --query primaryConnectionString -o tsv
