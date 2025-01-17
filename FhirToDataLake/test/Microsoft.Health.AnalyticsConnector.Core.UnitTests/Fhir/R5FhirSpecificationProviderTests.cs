﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.AnalyticsConnector.Common.Logging;
using Microsoft.Health.AnalyticsConnector.Core.Exceptions;
using Microsoft.Health.AnalyticsConnector.Core.Fhir.SpecificationProviders;
using Microsoft.Health.AnalyticsConnector.DataClient;
using Microsoft.Health.AnalyticsConnector.DataClient.Exceptions;
using Microsoft.Health.AnalyticsConnector.DataClient.Models.FhirApiOption;
using Microsoft.Health.AnalyticsConnector.DataClient.UnitTests;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Microsoft.Health.AnalyticsConnector.Core.UnitTests.Fhir
{
    public class R5FhirSpecificationProviderTests
    {
        private IDiagnosticLogger _diagnosticLogger = new DiagnosticLogger();
        private IFhirSpecificationProvider _r5FhirSpecificationProvider;

        private readonly NullLogger<R5FhirSpecificationProvider> _nullR5FhirSpecificationProviderLogger =
            NullLogger<R5FhirSpecificationProvider>.Instance;

        public R5FhirSpecificationProviderTests()
        {
            var dataClient = Substitute.For<IApiDataClient>();

            var metadataOptions = new MetadataOptions();
            dataClient.Search(metadataOptions)
                .ReturnsForAnyArgs(x => TestDataProvider.GetDataFromFile(TestDataConstants.R5MetadataFile));

            _r5FhirSpecificationProvider = new R5FhirSpecificationProvider(dataClient, _diagnosticLogger, _nullR5FhirSpecificationProviderLogger);
        }

        [Fact]
        public void GivenNullInputParameters_WhenInitialize_ExceptionShouldBeThrown()
        {
            Assert.Throws<ArgumentNullException>(
                () => new R5FhirSpecificationProvider(null, _diagnosticLogger, _nullR5FhirSpecificationProviderLogger));

            var dataClient = Substitute.For<IApiDataClient>();

            Assert.Throws<ArgumentNullException>(
                () => new R5FhirSpecificationProvider(dataClient, _diagnosticLogger, null));
        }

        [Fact]
        public void GivenBrokenDataClient_WhenInitialize_ExceptionShouldBeThrown()
        {
            var dataClient = Substitute.For<IApiDataClient>();
            dataClient.SearchAsync(default, default).ThrowsForAnyArgs(new ApiSearchException("mockException"));

            var provider = new R5FhirSpecificationProvider(dataClient, _diagnosticLogger, _nullR5FhirSpecificationProviderLogger);
            Assert.Throws<FhirSpecificationProviderException>(
                () => provider.GetSearchParametersByResourceType("Patient"));
        }

        [Theory]
        [InlineData("{\"invalidCapabilityStatement\": 0}")]
        [InlineData("{\"resourceType\": \"CapabilityStatement\"}")]
        [InlineData("{\"resourceType\": \"CapabilityStatement\",\"rest\": []}")]
        [InlineData("{\"resourceType\": \"CapabilityStatement\",\"rest\": [{\"mode\":\"server\"}]}")]
        public void GivenInvalidMetadata_WhenInitialize_ExceptionShouldBeThrown(string metadataContent)
        {
            var dataClient = Substitute.For<IApiDataClient>();
            dataClient.SearchAsync(default, default).ReturnsForAnyArgs(metadataContent);

            var provider = new R5FhirSpecificationProvider(dataClient, _diagnosticLogger, _nullR5FhirSpecificationProviderLogger);
            Assert.Throws<FhirSpecificationProviderException>(
                () => provider.GetSearchParametersByResourceType("Patient"));
        }

        [Fact]
        public void WhenGetAllResourceTypes_TheResourcesTypeShouldBeReturned()
        {
            List<string> types = _r5FhirSpecificationProvider.GetAllResourceTypes().ToList();
            Assert.Equal(150, types.Count);
        }

        [Fact]
        public void WhenGetAllResourceTypes_ExcludeResourceTypesShouldNotBeReturned()
        {
            List<string> types = _r5FhirSpecificationProvider.GetAllResourceTypes().ToList();
            foreach (string excludeType in TestUtils.ExcludeResourceTypes)
            {
                Assert.DoesNotContain(excludeType, types);
            }
        }

        [Theory]
        [InlineData("Patient")]
        [InlineData("InventoryReport")]
        [InlineData("EvidenceReport")]
        public void GivenValidResourceType_WhenCheckIsFhirResourceType_TrueShouldBeReturned(string type)
        {
            bool isValid = _r5FhirSpecificationProvider.IsValidFhirResourceType(type);
            Assert.True(isValid);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("invalidResourceType")]
        [InlineData("patient")]
        [InlineData("PATIENT")]
        [InlineData("Patient ")]
        public void GivenInvalidResourceType_WhenCheckIsFhirResourceType_FalseShouldBeReturned(string type)
        {
            bool isValid = _r5FhirSpecificationProvider.IsValidFhirResourceType(type);
            Assert.False(isValid);
        }

        [Fact]
        public void GivenValidCompartmentType_WhenGetCompartmentResourceTypes_ResourceTypesShouldBeReturned()
        {
            IEnumerable<string> types = _r5FhirSpecificationProvider.GetCompartmentResourceTypes("Patient");
            Assert.Equal(68, types.Count());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("invalidResourceType")]
        [InlineData("Account")]
        [InlineData("Devices")]
        [InlineData("RelatedPerson")]
        [InlineData("patient")]
        [InlineData("Patient ")]
        public void GivenInvalidCompartmentType_WhenGetCompartmentResourceTypes_ExceptionShouldBeThrown(string type)
        {
            Assert.Throws<FhirSpecificationProviderException>(() => _r5FhirSpecificationProvider.GetCompartmentResourceTypes(type));
        }

        [Theory]
        [InlineData("Patient", 29)]
        [InlineData("InventoryReport", 6)]
        [InlineData("EvidenceReport", 15)]
        public void GivenValidResourceType_WhenGetSearchParametersByResourceType_SearchParametersShouldBeReturned(
            string type,
            int cnt)
        {
            List<string> parameters = _r5FhirSpecificationProvider.GetSearchParametersByResourceType(type).ToList();
            Assert.NotEmpty(parameters);
            Assert.Equal(cnt, parameters.Count);
            Assert.Contains("_id", parameters);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("invalidResourceType")]
        [InlineData("patient")]
        [InlineData("Patient ")]
        public void GivenInvalidResourceType_WhenGetSearchParametersByResourceType_ExceptionShouldBeThrown(string type)
        {
            Assert.Throws<FhirSpecificationProviderException>(() => _r5FhirSpecificationProvider.GetSearchParametersByResourceType(type));
        }
    }
}
