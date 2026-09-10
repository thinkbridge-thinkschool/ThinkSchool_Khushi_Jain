type alertSettings = {
  errorRatePercent: int?
  severity: int?
  evaluationFrequency: string?
  windowSize: string?
}

@description('The location used for all deployed resources')
param location string

@description('Tags that will be applied to all resources')
param tags object = {}

@description('Name of the error-rate alert rule')
param name string

@description('Name of the action group the rule notifies')
param actionGroupName string

@description('Application Insights resource the rule queries')
param applicationInsightsResourceId string

@description('Address that receives the alert')
param notificationEmail string

@description('Environment overrides; anything omitted falls back to the defaults below')
param settings alertSettings = {}

var defaults = {
  errorRatePercent: 5
  severity: 2
  evaluationFrequency: 'PT5M'
  windowSize: 'PT15M'
}

var alert = union(defaults, settings)

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: actionGroupName
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'quotesapi'
    enabled: true
    emailReceivers: [
      {
        name: 'owner'
        emailAddress: notificationEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

resource errorRate 'Microsoft.Insights/scheduledQueryRules@2021-08-01' = {
  name: name
  location: location
  tags: tags
  kind: 'LogAlert'
  properties: {
    displayName: name
    description: 'Failed share of requests over the evaluation window'
    severity: alert.severity
    enabled: true
    scopes: [applicationInsightsResourceId]
    evaluationFrequency: alert.evaluationFrequency
    windowSize: alert.windowSize
    autoMitigate: true
    criteria: {
      allOf: [
        {
          // No time filter in the query: the rule applies windowSize itself, and a second filter would narrow it again.
          query: '''
            requests
            | summarize total = count(), failed = countif(success == false)
            | extend errorRatePercent = iff(total == 0, 0.0, 100.0 * failed / total)
            | project errorRatePercent
          '''
          timeAggregation: 'Average'
          metricMeasureColumn: 'errorRatePercent'
          operator: 'GreaterThan'
          threshold: alert.errorRatePercent
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [actionGroup.id]
    }
  }
}

output actionGroupName string = actionGroup.name
output ruleName string = errorRate.name
