#!/usr/bin/env bash
set -euo pipefail

group=rg-day21-cache

# The round trip to L2 is part of what this measures, so pick the region nearest you.
location=${AZURE_LOCATION:-eastus}

suffix=$(az account show --query id -o tsv | tr -d '-' | cut -c1-12)
name="redisday21$suffix"

echo "Cache $name in $group ($location). Creation takes 15-20 minutes." >&2

# A subscription registers a resource provider once. Microsoft.Cache is not on by default.
az provider register --namespace Microsoft.Cache --wait -o none

az group create --name "$group" --location "$location" -o none

# Basic C0, the smallest size there is: one node, no replica, no SLA. It bills by
# the hour whether or not anything reads it, so delete the group after the run.
az redis create --name "$name" --resource-group "$group" --location "$location" \
    --sku Basic --vm-size c0 --minimum-tls-version 1.2 -o none

host=$(az redis show --name "$name" --resource-group "$group" --query hostName -o tsv)
key=$(az redis list-keys --name "$name" --resource-group "$group" --query primaryKey -o tsv)

echo "Cache ready at $host." >&2

echo "$host:6380,password=$key,ssl=True,abortConnect=False"
