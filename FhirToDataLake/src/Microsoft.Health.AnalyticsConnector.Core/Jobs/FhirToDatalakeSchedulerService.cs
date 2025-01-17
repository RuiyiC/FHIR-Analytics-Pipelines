﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.AnalyticsConnector.Common.Configurations;
using Microsoft.Health.AnalyticsConnector.Common.Logging;
using Microsoft.Health.AnalyticsConnector.Common.Metrics;
using Microsoft.Health.AnalyticsConnector.Core.Extensions;
using Microsoft.Health.AnalyticsConnector.Core.Jobs.Models;
using Microsoft.Health.AnalyticsConnector.Core.Jobs.Models.AzureStorage;
using Microsoft.Health.JobManagement;
using NCrontab;
using Newtonsoft.Json;

namespace Microsoft.Health.AnalyticsConnector.Core.Jobs
{
    public class FhirToDatalakeSchedulerService : ISchedulerService
    {
        private readonly IQueueClient _queueClient;

        private readonly IMetadataStore _metadataStore;
        private readonly byte _queueType;
        private readonly DateTimeOffset? _startTime;
        private readonly DateTimeOffset? _endTime;
        private readonly ILogger<FhirToDatalakeSchedulerService> _logger;
        private readonly IDiagnosticLogger _diagnosticLogger;
        private readonly IMetricsLogger _metricsLogger;
        private readonly Guid _instanceGuid;

        // See https://github.com/atifaziz/NCrontab/wiki/Crontab-Expression
        private readonly CrontabSchedule _crontabSchedule;

        public FhirToDatalakeSchedulerService(
            IQueueClient queueClient,
            IMetadataStore metadataStore,
            IOptions<JobConfiguration> jobConfiguration,
            IMetricsLogger metricsLogger,
            IDiagnosticLogger diagnosticLogger,
            ILogger<FhirToDatalakeSchedulerService> logger)
        {
            _queueClient = EnsureArg.IsNotNull(queueClient, nameof(queueClient));
            _metadataStore = EnsureArg.IsNotNull(metadataStore, nameof(metadataStore));

            EnsureArg.IsNotNull(jobConfiguration, nameof(jobConfiguration));
            _queueType = (byte)jobConfiguration.Value.QueueType;

            // if both startTime and endTime are specified, add validation on ConfigurationValidatorV1 to make sure startTime is larger than endTime.
            _startTime = jobConfiguration.Value.StartTime;
            _endTime = jobConfiguration.Value.EndTime;

            // add validation on ConfiguraionValidatorV1 to make sure the SchedulerCronExpression is valid.
            _crontabSchedule = CrontabSchedule.Parse(jobConfiguration.Value.SchedulerCronExpression, new CrontabSchedule.ParseOptions { IncludingSeconds = true });
            _diagnosticLogger = EnsureArg.IsNotNull(diagnosticLogger, nameof(diagnosticLogger));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
            _instanceGuid = Guid.NewGuid();
            _metricsLogger = EnsureArg.IsNotNull(metricsLogger, nameof(metricsLogger));
        }

        public DateTimeOffset LastHeartbeat { get; set; } = DateTimeOffset.UtcNow;

        public int SchedulerServicePullingIntervalInSeconds { get; set; } = JobConfigurationConstants.DefaultSchedulerServicePullingIntervalInSeconds;

        public int SchedulerServiceLeaseExpirationInSeconds { get; set; } = JobConfigurationConstants.DefaultSchedulerServiceLeaseExpirationInSeconds;

        public int SchedulerServiceLeaseRefreshIntervalInSeconds { get; set; } = JobConfigurationConstants.DefaultSchedulerServiceLeaseRefreshIntervalInSeconds;

        // scheduler service is a long running service, it shouldn't stop for any exception.
        // It stops only when the job is scheduled to end or is cancelled.
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _diagnosticLogger.LogInformation($"Scheduler instance {_instanceGuid} starts running.");
            _logger.LogInformation($"Scheduler instance {_instanceGuid} starts running.");

            using var delayTaskCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken delayTaskCancellationToken = delayTaskCancellationTokenSource.Token;

            bool scheduledToEnd = false;
            while (!scheduledToEnd && !cancellationToken.IsCancellationRequested)
            {
                // delay task should stop without throwing exception when is cancellation requested.
                Task delayTask = Task.Delay(TimeSpan.FromSeconds(SchedulerServicePullingIntervalInSeconds), delayTaskCancellationToken).ContinueWith(_ => { });

                try
                {
                    // try next time if the storage aren't initialized.
                    if (!_queueClient.IsInitialized() || !_metadataStore.IsInitialized())
                    {
                        _logger.LogInformation("The storage isn't initialized.");
                    }
                    else
                    {
                        bool leaseAcquired = await TryAcquireLeaseAsync(cancellationToken);

                        if (leaseAcquired)
                        {
                            scheduledToEnd = await ProcessInternalAsync(cancellationToken);
                            if (scheduledToEnd)
                            {
                                delayTaskCancellationTokenSource.Cancel();
                            }
                        }
                    }
                }
                catch (OperationCanceledException ex)
                {
                    delayTaskCancellationTokenSource.Cancel();
                    _diagnosticLogger.LogError($"Scheduler service {_instanceGuid} is cancelled.");
                    _logger.LogError($"Scheduler service {_instanceGuid} is cancelled.");
                    _metricsLogger.LogTotalErrorsMetrics(ex, $"Scheduler service {_instanceGuid} is cancelled.", JobOperations.RunSchedulerService);
                }
                catch (Exception ex)
                {
                    _diagnosticLogger.LogError($"Internal error occurred in scheduler service {_instanceGuid}, will retry later.");
                    _logger.LogError(ex, $"There is an exception thrown in scheduler instance {_instanceGuid}, will retry later.");
                    _metricsLogger.LogTotalErrorsMetrics(ex, $"There is an exception thrown in scheduler instance {_instanceGuid}, will retry later.", JobOperations.RunSchedulerService);
                }

                await delayTask;

                LastHeartbeat = DateTimeOffset.UtcNow;
            }

            _diagnosticLogger.LogInformation($"Scheduler instance {_instanceGuid} stops running.");
            _logger.LogInformation($"Scheduler instance {_instanceGuid} stops running.");
        }

        /// <summary>
        /// try to acquire lease
        /// </summary>
        /// <param name="cancellationToken">cancellation token</param>
        /// <returns>return true if lease is acquired, otherwise return false, will throw unexpected or cancellation exception.</returns>
        private async Task<bool> TryAcquireLeaseAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Scheduler instance {_instanceGuid} starts to acquire lease.");

            // try to get trigger lease entity
            TriggerLeaseEntity triggerLeaseEntity = await _metadataStore.GetTriggerLeaseEntityAsync(_queueType, cancellationToken);

            // if the trigger lease entity doesn't exist, create it and try to get it again
            if (triggerLeaseEntity == null)
            {
                // if there are two instances try to create initialize trigger lease entity, then only one instance will success,
                // the failed one will find the triggerLeaseEntity's WorkingInstanceGuid doesn't match in the below step and return false;
                await CreateInitialTriggerLeaseEntityAsync(cancellationToken);

                triggerLeaseEntity = await _metadataStore.GetTriggerLeaseEntityAsync(_queueType, cancellationToken);
            }

            if (triggerLeaseEntity == null)
            {
                _logger.LogInformation($"Scheduler instance {_instanceGuid} fails to acquire lease, failed to get trigger lease entity.");
                return false;
            }

            if (triggerLeaseEntity.WorkingInstanceGuid != _instanceGuid &&
                triggerLeaseEntity.HeartbeatDateTime.AddSeconds(SchedulerServiceLeaseExpirationInSeconds) > DateTimeOffset.UtcNow)
            {
                _logger.LogInformation($"Scheduler instance {_instanceGuid} fails to acquire lease, another scheduler instance {triggerLeaseEntity.WorkingInstanceGuid} is using it.");
                return false;
            }

            triggerLeaseEntity.WorkingInstanceGuid = _instanceGuid;
            triggerLeaseEntity.HeartbeatDateTime = DateTimeOffset.UtcNow;

            // if the existing lease is timeout, and there are two instances try to acquire the lease and update the entity, only one will success, and the failed one will return false.
            bool isSucceeded = await _metadataStore.TryUpdateEntityAsync(triggerLeaseEntity, cancellationToken);
            _logger.LogInformation(isSucceeded
                ? $"Scheduler instance {_instanceGuid} acquires lease successfully."
                : $"Scheduler instance {_instanceGuid} fails to acquire lease, failed to update lease trigger entity: {triggerLeaseEntity}.");

            return isSucceeded;
        }

        /// <summary>
        /// Internal function for processing
        /// </summary>
        /// <param name="cancellationToken">cancellation token</param>
        /// <returns>return true if The job has been scheduled to end, otherwise return false</returns>
        private async Task<bool> ProcessInternalAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Scheduler instance {_instanceGuid} starts internal processing.");

            using var runningCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken runningCancellationToken = runningCancellationTokenSource.Token;

            // renew lease task will complete if cancelled or any exception
            Task renewLeaseTask = RenewLeaseAsync(runningCancellationToken);

            // process trigger task will complete if cancelled or the job has been scheduled to end.
            Task<bool> processTriggerTask = CheckAndUpdateTriggerEntityAsync(runningCancellationToken);

            // if one of these two tasks returns, should cancel the other one.
            Task finishedTask = await Task.WhenAny(renewLeaseTask, processTriggerTask);

            if (finishedTask == renewLeaseTask)
            {
                _logger.LogInformation($"Scheduler instance {_instanceGuid} stops to check and update trigger entity as it failed to renew lease.");
            }

            runningCancellationTokenSource.Cancel();

            await Task.WhenAll(renewLeaseTask, processTriggerTask);

            _logger.LogInformation($"Scheduler instance {_instanceGuid} stops internal processing.");

            return processTriggerTask.Result;
        }

        /// <summary>
        /// Add the initial trigger entity to table
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>return true if add entity successfully, return false if the entity already exists.</returns>
        private async Task<bool> CreateInitialTriggerLeaseEntityAsync(CancellationToken cancellationToken)
        {
            var initialTriggerLeaseEntity = new TriggerLeaseEntity
            {
                PartitionKey = TableKeyProvider.LeasePartitionKey(_queueType),
                RowKey = TableKeyProvider.LeaseRowKey(_queueType),
                WorkingInstanceGuid = _instanceGuid,
                HeartbeatDateTime = DateTimeOffset.UtcNow,
            };

            bool isSucceeded = await _metadataStore.TryAddEntityAsync(initialTriggerLeaseEntity, cancellationToken);

            _logger.LogInformation(isSucceeded
                ? "Create initial trigger lease entity successfully."
                : "Failed to create initial trigger lease entity, the entity already exists.");

            return isSucceeded;
        }

        /// <summary>
        /// a while-loop to check and update trigger entity
        /// </summary>
        /// <param name="cancellationToken">cancellation token</param>
        /// <returns>return true if The job has been scheduled to end, otherwise return false</returns>
        private async Task<bool> CheckAndUpdateTriggerEntityAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Scheduler instance {_instanceGuid} starts to check and update trigger entity in loop.");
            using var delayTaskCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken delayTaskCancellationToken = delayTaskCancellationTokenSource.Token;
            bool scheduledToEnd = false;
            while (!scheduledToEnd && !cancellationToken.IsCancellationRequested)
            {
                Task intervalDelayTask = Task.Delay(TimeSpan.FromSeconds(SchedulerServicePullingIntervalInSeconds), delayTaskCancellationToken).ContinueWith(_ => { });

                try
                {
                    scheduledToEnd = await CheckAndUpdateTriggerEntityInternalAsync(cancellationToken);
                    if (scheduledToEnd)
                    {
                        delayTaskCancellationTokenSource.Cancel();
                    }
                }
                catch (OperationCanceledException)
                {
                    delayTaskCancellationTokenSource.Cancel();
                }
                catch (Exception ex)
                {
                    // don't exist the while-loop, and retry next time
                    _diagnosticLogger.LogError($"Internal error occurred in scheduler service {_instanceGuid}, will retry later.");
                    _logger.LogError(ex, $"There is an exception thrown in scheduler instance {_instanceGuid}, will retry later.");
                    _metricsLogger.LogTotalErrorsMetrics(ex, $"There is an exception thrown in scheduler instance {_instanceGuid}, will retry later.", JobOperations.RunSchedulerService);
                }

                await intervalDelayTask;

                LastHeartbeat = DateTimeOffset.UtcNow;
            }

            _logger.LogInformation($"Scheduler instance {_instanceGuid} stops to check and update trigger entity in loop.");

            return scheduledToEnd;
        }

        /// <summary>
        /// check and update trigger entity
        /// </summary>
        /// <param name="cancellationToken">cancellation token</param>
        /// <returns>return true if The job has been scheduled to end, otherwise return false</returns>
        private async Task<bool> CheckAndUpdateTriggerEntityInternalAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Scheduler instance {_instanceGuid} starts to check and update trigger entity.");

            CurrentTriggerEntity currentTriggerEntity = await _metadataStore.GetCurrentTriggerEntityAsync(_queueType, cancellationToken) ??
                                                        await CreateInitialTriggerEntity(cancellationToken);

            // if current trigger entity is still null after initializing, just skip processing it this time and try again next time.
            // this case should not happen.
            bool scheduledToEnd = false;
            if (currentTriggerEntity != null)
            {
                switch (currentTriggerEntity.TriggerStatus)
                {
                    case TriggerStatus.New:
                        await EnqueueOrchestratorJobAsync(currentTriggerEntity, cancellationToken);
                        break;
                    case TriggerStatus.Running:
                        await CheckAndUpdateOrchestratorJobStatusAsync(currentTriggerEntity, cancellationToken);
                        break;
                    case TriggerStatus.Completed:
                        scheduledToEnd = await CreateNextTriggerAsync(currentTriggerEntity, cancellationToken);
                        break;
                    case TriggerStatus.Failed:
                    case TriggerStatus.Cancelled:
                        // if the current trigger is cancelled/failed, do noting and keep running, this case should not happen
                        _logger.LogInformation("Trigger Status is cancelled or failed.");
                        break;
                }
            }

            _logger.LogInformation($"Scheduler instance {_instanceGuid} finishes checking and updating trigger entity.");

            return scheduledToEnd;
        }

        private async Task EnqueueOrchestratorJobAsync(CurrentTriggerEntity currentTriggerEntity, CancellationToken cancellationToken)
        {
            // enqueue a orchestrator job for this trigger
            var orchestratorDefinition = new FhirToDataLakeOrchestratorJobInputData
            {
                JobType = JobType.Orchestrator,
                JobVersion = currentTriggerEntity.JobVersion,
                TriggerSequenceId = currentTriggerEntity.TriggerSequenceId,
                Since = _startTime,
                DataStartTime = currentTriggerEntity.TriggerStartTime,
                DataEndTime = currentTriggerEntity.TriggerEndTime,
            };

            IEnumerable<JobInfo> jobInfoList = await _queueClient.EnqueueAsync(
                _queueType,
                new[] { JsonConvert.SerializeObject(orchestratorDefinition) },
                currentTriggerEntity.TriggerSequenceId,
                false,
                false,
                cancellationToken);

            currentTriggerEntity.OrchestratorJobId = jobInfoList.First().Id;
            currentTriggerEntity.TriggerStatus = TriggerStatus.Running;

            bool isSucceeded = await _metadataStore.TryUpdateEntityAsync(currentTriggerEntity, cancellationToken);

            _logger.LogInformation(isSucceeded
                ? $"Enqueue orchestrator job for trigger sequence id {currentTriggerEntity.TriggerSequenceId}, the orchestrator job id is {currentTriggerEntity.OrchestratorJobId}."
                : $"Failed to enqueue orchestrator job for trigger sequence id {currentTriggerEntity.TriggerSequenceId}, failed to update current trigger entity.");
        }

        private async Task CheckAndUpdateOrchestratorJobStatusAsync(CurrentTriggerEntity currentTriggerEntity, CancellationToken cancellationToken)
        {
            long orchestratorJobId = currentTriggerEntity.OrchestratorJobId;
            JobInfo orchestratorJob = await _queueClient.GetJobByIdAsync(
                _queueType,
                orchestratorJobId,
                false,
                cancellationToken);

            bool needUpdateTriggerEntity = true;

            // job status "Archived" is useless,
            // shouldn't set job status to archived, if do, need to update the handle logic here
            switch (orchestratorJob.Status)
            {
                case JobStatus.Completed:
                    currentTriggerEntity.TriggerStatus = TriggerStatus.Completed;
                    _logger.LogInformation($"Current trigger with sequence id {currentTriggerEntity.TriggerSequenceId} is completed.");
                    break;
                case JobStatus.Failed:
                    currentTriggerEntity.TriggerStatus = TriggerStatus.Failed;
                    _logger.LogInformation($"The orchestrator job of trigger with sequence id {currentTriggerEntity.TriggerSequenceId} is failed.");
                    break;
                case JobStatus.Cancelled:
                    currentTriggerEntity.TriggerStatus = TriggerStatus.Cancelled;
                    _logger.LogInformation($"The orchestrator job trigger with sequence id {currentTriggerEntity.TriggerSequenceId} is cancelled.");
                    break;
                default:
                    needUpdateTriggerEntity = false;
                    break;
            }

            if (needUpdateTriggerEntity)
            {
                bool isSucceeded = await _metadataStore.TryUpdateEntityAsync(currentTriggerEntity, cancellationToken);
                _logger.LogInformation(isSucceeded
                    ? $"Update current trigger entity successfully: {currentTriggerEntity}."
                    : $"Failed to update current trigger entity, Etag precondition failed: {currentTriggerEntity}.");
            }
        }

        /// <summary>
        /// Create the next trigger entity
        /// </summary>
        /// <param name="currentTriggerEntity">current trigger entity.</param>
        /// <param name="cancellationToken">cancellation token.</param>
        /// <returns>return true if the job has been scheduled to end, otherwise return false.</returns>
        private async Task<bool> CreateNextTriggerAsync(CurrentTriggerEntity currentTriggerEntity, CancellationToken cancellationToken)
        {
            DateTimeOffset? nextTriggerEndTime = GetNextTriggerEndTime(currentTriggerEntity.TriggerEndTime);

            if (nextTriggerEndTime != null)
            {
                DateTimeOffset? nextTriggerStartTime = GetNextTriggerStartTime(currentTriggerEntity.TriggerEndTime);

                if (nextTriggerStartTime >= nextTriggerEndTime)
                {
                    _logger.LogInformation($"The job has been scheduled to end {nextTriggerStartTime}.");
                    return true;
                }

                currentTriggerEntity.TriggerSequenceId += 1;
                currentTriggerEntity.TriggerStatus = TriggerStatus.New;

                currentTriggerEntity.TriggerStartTime = nextTriggerStartTime;
                currentTriggerEntity.TriggerEndTime = (DateTimeOffset)nextTriggerEndTime;
                currentTriggerEntity.JobVersion = FhirToDatalakeJobVersionManager.CurrentJobVersion;

                bool isSucceeded = await _metadataStore.TryUpdateEntityAsync(currentTriggerEntity, cancellationToken);
                _logger.LogInformation(isSucceeded
                    ? $"A new trigger with sequence id {currentTriggerEntity.TriggerSequenceId} is updated to azure table."
                    : $"Failed to update new trigger with sequence id {currentTriggerEntity.TriggerSequenceId}, Etag precondition failed: {currentTriggerEntity}.");
            }

            return false;
        }

        /// <summary>
        /// Create initial trigger entity
        /// </summary>
        /// <param name="cancellationToken">cancellation token</param>
        /// <returns>The created trigger entity</returns>
        private async Task<CurrentTriggerEntity> CreateInitialTriggerEntity(CancellationToken cancellationToken)
        {
            var initialTriggerEntity = new CurrentTriggerEntity
            {
                PartitionKey = TableKeyProvider.TriggerPartitionKey(_queueType),
                RowKey = TableKeyProvider.TriggerPartitionKey(_queueType),
                TriggerStartTime = GetNextTriggerStartTime(null),
                TriggerEndTime = (DateTimeOffset)GetNextTriggerEndTime(null),
                TriggerStatus = TriggerStatus.New,
                TriggerSequenceId = 0,
                JobVersion = FhirToDatalakeJobVersionManager.CurrentJobVersion,
            };

            // add the initial trigger entity to table
            bool isSucceeded = await _metadataStore.TryAddEntityAsync(initialTriggerEntity, cancellationToken);

            _logger.LogInformation(isSucceeded
                ? "Create initial trigger entity successfully."
                : "Failed to create initial trigger entity, the entity already exists.");

            return await _metadataStore.GetCurrentTriggerEntityAsync(_queueType, cancellationToken);
        }

        /// <summary>
        /// return the start time in configuration for initial load job, otherwise return the last trigger end time
        /// </summary>
        private DateTimeOffset? GetNextTriggerStartTime(DateTimeOffset? lastTriggerEndTime)
        {
            return lastTriggerEndTime ?? _startTime;
        }

        /// <summary>
        /// FHIR data use processing time as lastUpdated timestamp, there might be some latency when saving to data store.
        /// Here we add a JobEndTimeLatencyInMinutes latency to avoid data missing due to latency in creation.
        /// So the nextTriggerEndTime is set JobEndTimeLatencyInMinutes earlier than the current timestamp.
        /// If Job end time is specified and earlier than the nextTriggerEndTime, will set the nextTriggerEndTime to the specified job end time.
        /// For initial load job, lastTriggerEndTime is null;
        /// For incremental job, lastTriggerEndTime is the end time of the last trigger, we also get all the next occurrence times during lastTriggerEndTime and latency utc now.
        /// If next occurrence time comes after latency utc now, will return null and skip this iteration.
        /// </summary>
        /// <param name="lastTriggerEndTime">the end time of the last trigger</param>
        /// <returns>the end time of the next trigger, return null if it is not yet the next occurrence time</returns>
        private DateTimeOffset? GetNextTriggerEndTime(DateTimeOffset? lastTriggerEndTime)
        {
            // Add latency to utc now
            DateTimeOffset nextTriggerEndTime =
                DateTimeOffset.UtcNow.AddMinutes(-1 * JobConfigurationConstants.JobQueryLatencyInMinutes);

            // set the trigger end time for incremental job
            if (lastTriggerEndTime != null)
            {
                // this functions will return times > baseTime and < endTime
                DateTime nextOccurrenceTime = _crontabSchedule.GetNextOccurrences(((DateTimeOffset)lastTriggerEndTime).DateTime, nextTriggerEndTime.DateTime).LastOrDefault();
                if (nextOccurrenceTime == default)
                {
                    return null;
                }

                DateTimeOffset nextOccurrenceOffsetTime = DateTime.SpecifyKind(nextOccurrenceTime, DateTimeKind.Utc);

                nextTriggerEndTime = nextOccurrenceOffsetTime;
            }

            if (nextTriggerEndTime > _endTime)
            {
                nextTriggerEndTime = (DateTimeOffset)_endTime;
            }

            return nextTriggerEndTime;
        }

        /// <summary>
        /// A while-loop to renew lease, stop and return if cancelled or any exception, should not throw any exception
        /// </summary>
        private async Task RenewLeaseAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Scheduler instance {_instanceGuid} starts to renew lease in loop.");
            using var delayTaskCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken delayTaskCancellationToken = delayTaskCancellationTokenSource.Token;
            while (!cancellationToken.IsCancellationRequested && !delayTaskCancellationToken.IsCancellationRequested)
            {
                Task intervalDelayTask =
                    Task.Delay(TimeSpan.FromSeconds(SchedulerServiceLeaseRefreshIntervalInSeconds), delayTaskCancellationToken).ContinueWith(_ => { });
                try
                {
                    // if renew lease successfully, then delay some time to renew again
                    // if fail to renew lease, then stop renew and return
                    // if there is an exception while renewing lease, then stop renew and return
                    // if cancelled, the cancel exception will be caught and then stop renew and return.
                    bool isSucceeded = await TryRenewLeaseInternalAsync(cancellationToken);
                    if (!isSucceeded)
                    {
                        delayTaskCancellationTokenSource.Cancel();
                    }
                }
                catch (Exception ex)
                {
                    delayTaskCancellationTokenSource.Cancel();
                    _logger.LogInformation(ex, $"Scheduler instance {_instanceGuid} fails to renew lease.");
                }

                await intervalDelayTask;
            }

            _logger.LogInformation($"Scheduler instance {_instanceGuid} stops to renew lease in loop.");
        }

        /// <summary>
        /// try to renew lease
        /// </summary>
        /// <param name="cancellationToken">cancellation token.</param>
        /// <returns>return true if the lease is renewed, otherwise return false or throw unknown exceptions</returns>
        private async Task<bool> TryRenewLeaseInternalAsync(CancellationToken cancellationToken)
        {
            // try to get trigger lease entity
            TriggerLeaseEntity triggerLeaseEntity =
                await _metadataStore.GetTriggerLeaseEntityAsync(_queueType, cancellationToken);

            if (triggerLeaseEntity == null)
            {
                _logger.LogInformation($"Scheduler instance {_instanceGuid} fails to renew lease, the retrieved lease trigger entity is null.");
                return false;
            }

            if (triggerLeaseEntity.WorkingInstanceGuid != _instanceGuid)
            {
                _logger.LogInformation($"Scheduler instance {_instanceGuid} Fails to renew lease, the retrieved lease trigger entity's working instance {triggerLeaseEntity.WorkingInstanceGuid} doesn't match.");
                return false;
            }

            triggerLeaseEntity.HeartbeatDateTime = DateTimeOffset.UtcNow;

            bool isSucceeded = await _metadataStore.TryUpdateEntityAsync(
                triggerLeaseEntity,
                cancellationToken: cancellationToken);

            _logger.LogInformation(isSucceeded
                ? $"Scheduler instance {_instanceGuid} renews lease successfully."
                : $"Scheduler instance {_instanceGuid} fails to renew lease, failed to update lease trigger entity: {triggerLeaseEntity}.");
            return isSucceeded;
        }
    }
}