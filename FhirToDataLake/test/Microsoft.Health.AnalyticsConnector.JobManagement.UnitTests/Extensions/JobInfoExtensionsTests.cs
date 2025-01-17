﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Azure.Data.Tables;
using Microsoft.Health.AnalyticsConnector.Common.Models.Jobs;
using Microsoft.Health.AnalyticsConnector.JobManagement.Extensions;
using Microsoft.Health.AnalyticsConnector.JobManagement.Models;
using Microsoft.Health.AnalyticsConnector.JobManagement.Models.AzureStorage;
using Microsoft.Health.JobManagement;
using Xunit;

namespace Microsoft.Health.AnalyticsConnector.JobManagement.UnitTests.Extensions
{
    public class JobInfoExtensionsTests
    {
        [Fact]
        public void GivenNullJobInfo_WhenToTableEntity_ThenTheExceptionShouldBeThrown()
        {
            AzureStorageJobInfo? jobInfo = null;
#pragma warning disable CS8631
            Assert.Throws<NullReferenceException>(() => jobInfo.ToTableEntity());
#pragma warning restore CS8631
        }

        [Fact]
        public void GivenDefaultJobInfo_WhenToTableEntity_ThenTheCorrectTableEntityShouldBeReturned()
        {
            var jobInfo = new AzureStorageJobInfo();
            var tableEntity = jobInfo.ToTableEntity();
            Assert.NotNull(tableEntity);
            Assert.Null(jobInfo.Status);
            Assert.Equal((int)JobStatus.Created, (int)tableEntity[JobInfoEntityProperties.Status]);
            Assert.Null(tableEntity[JobInfoEntityProperties.Definition]);
            Assert.Null(tableEntity[JobInfoEntityProperties.Result]);
        }

        [Fact]
        public void GivenValidJobInfo_WhenToTableEntity_ThenTheCorrectTableEntityShouldBeReturned()
        {
            var jobInfo = new AzureStorageJobInfo
            {
                Id = 1,
                QueueType = (byte)QueueType.FhirToDataLake,
                Status = JobStatus.Created,
                GroupId = 123,
                Definition = "input data string",
                Result = "result string",
                CancelRequested = true,
                CreateDate = DateTime.UtcNow,
                HeartbeatDateTime = DateTime.UtcNow,
            };
            var tableEntity = jobInfo.ToTableEntity();
            Assert.NotNull(tableEntity);
            Assert.Equal(jobInfo.Id, (long)tableEntity[JobInfoEntityProperties.Id]);
            Assert.Equal(jobInfo.QueueType, (int)tableEntity[JobInfoEntityProperties.QueueType]);
            Assert.Equal((int)jobInfo.Status, (int)tableEntity[JobInfoEntityProperties.Status]);
            Assert.Equal(jobInfo.GroupId, (long)tableEntity[JobInfoEntityProperties.GroupId]);
            Assert.Equal(jobInfo.Definition, tableEntity[JobInfoEntityProperties.Definition].ToString());
            Assert.Equal(jobInfo.Result, tableEntity[JobInfoEntityProperties.Result].ToString());
            Assert.Null(tableEntity[JobInfoEntityProperties.Data]);
            Assert.Equal(jobInfo.CancelRequested, (bool)tableEntity[JobInfoEntityProperties.CancelRequested]);
            Assert.Equal(0, (long)tableEntity[JobInfoEntityProperties.Version]);
            Assert.Equal(0, (long)tableEntity[JobInfoEntityProperties.Priority]);
            Assert.Equal(jobInfo.CreateDate, (DateTimeOffset)tableEntity[JobInfoEntityProperties.CreateDate]);
            Assert.Null(tableEntity[JobInfoEntityProperties.StartDate]);
            Assert.Null(tableEntity[JobInfoEntityProperties.EndDate]);
            Assert.Equal(jobInfo.HeartbeatDateTime, (DateTimeOffset)tableEntity[JobInfoEntityProperties.HeartbeatDateTime]);
            Assert.Equal(0, (long)tableEntity[JobInfoEntityProperties.HeartbeatTimeoutSec]);
        }

        [Fact]
        public void GivenNullTableEntity_WhenToJobInfo_ThenTheExceptionShouldBeThrown()
        {
            TableEntity? jobInfoEntity = null;
#pragma warning disable CS8604
            Assert.Throws<NullReferenceException>(() => jobInfoEntity.ToJobInfo<AzureStorageJobInfo>());
#pragma warning restore CS8604
        }

        [Fact]
        public void GivenValidTableEntity_WhenToJobInfo_ThenTheCorrectResultShouldBeReturned()
        {
            var jobInfoEntity = new TableEntity("partitionKey", "rowKey")
            {
                { JobInfoEntityProperties.Id, 101L },
                { JobInfoEntityProperties.QueueType, (int)QueueType.FhirToDataLake },
                { JobInfoEntityProperties.Status, (int)JobStatus.Running },
                { JobInfoEntityProperties.GroupId, 1L },
                { JobInfoEntityProperties.Definition, "input data string" },
                { JobInfoEntityProperties.Result, "result string" },
                { JobInfoEntityProperties.CancelRequested, false },
                { JobInfoEntityProperties.Version, 0L },
                { JobInfoEntityProperties.Priority, 0L },
                { JobInfoEntityProperties.CreateDate, DateTimeOffset.Now },
                { JobInfoEntityProperties.HeartbeatDateTime, DateTimeOffset.Now },
                { JobInfoEntityProperties.HeartbeatTimeoutSec, 0L },
            };
            var jobInfo = jobInfoEntity.ToJobInfo<AzureStorageJobInfo>();
            Assert.NotNull(jobInfo);
            Assert.Equal((long)jobInfoEntity[JobInfoEntityProperties.Id], jobInfo.Id);
            Assert.Equal((int)jobInfoEntity[JobInfoEntityProperties.QueueType], jobInfo.QueueType);
            Assert.Equal(JobStatus.Running, jobInfo.Status);
            Assert.Equal((long)jobInfoEntity[JobInfoEntityProperties.GroupId], jobInfo.GroupId);
            Assert.Equal(jobInfoEntity[JobInfoEntityProperties.Definition].ToString(), jobInfo.Definition);
            Assert.Equal(jobInfoEntity[JobInfoEntityProperties.Result].ToString(), jobInfo.Result);
            Assert.Null(jobInfo.Data);
            Assert.Equal((bool)jobInfoEntity[JobInfoEntityProperties.CancelRequested], jobInfo.CancelRequested);
            Assert.Equal((DateTimeOffset)jobInfoEntity[JobInfoEntityProperties.CreateDate], jobInfo.CreateDate);
            Assert.Null(jobInfo.StartDate);
            Assert.Null(jobInfo.EndDate);
            Assert.Equal((DateTimeOffset)jobInfoEntity[JobInfoEntityProperties.HeartbeatDateTime], jobInfo.HeartbeatDateTime);
            Assert.Equal((long)jobInfoEntity[JobInfoEntityProperties.HeartbeatTimeoutSec], jobInfo.HeartbeatTimeoutSec);
        }
    }
}