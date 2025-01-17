﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotLiquid;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.AnalyticsConnector.Common;
using Microsoft.Health.AnalyticsConnector.Common.Configurations;
using Microsoft.Health.AnalyticsConnector.Common.Logging;
using Microsoft.Health.AnalyticsConnector.Core.DataProcessor.DataConverter;
using Microsoft.Health.AnalyticsConnector.Core.Fhir;
using Microsoft.Health.AnalyticsConnector.SchemaManagement.ContainerRegistry;
using Microsoft.Health.AnalyticsConnector.SchemaManagement.Parquet;
using Microsoft.Health.AnalyticsConnector.SchemaManagement.Parquet.SchemaProvider;
using Microsoft.Health.Fhir.TemplateManagement;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;

namespace Microsoft.Health.AnalyticsConnector.Core.UnitTests
{
    public static class TestUtils
    {
        private static readonly IDiagnosticLogger _diagnosticLogger = new DiagnosticLogger();

        public const string TestDataFolder = "./TestData";
        public const string DicomTestDataFolder = TestDataFolder + "/Dicom";
        public const string ExpectTestDataFolder = TestDataFolder + "/Expected";
        public const string TestNormalSchemaDirectoryPath = TestDataFolder + "/Schema";
        public const string TestInvalidSchemaDirectoryPath = TestDataFolder + "/InvalidSchema";
        public const string TestCustomizedSchemaDirectoryPath = TestDataFolder + "/CustomizedSchema";
        public const string TestFilterTarGzPath = TestDataFolder + "/filter.tar.gz";

        public static readonly SchemaConfiguration TestDefaultSchemaConfiguration = new SchemaConfiguration();
        public static readonly SchemaConfiguration TestCustomSchemaConfiguration = new SchemaConfiguration()
        {
            EnableCustomizedSchema = true,
            SchemaImageReference = "testacr.azurecr.io/customizedtemplate:default",
        };

        public static readonly List<string> ExcludeResourceTypes = new List<string>
        {
            FhirConstants.StructureDefinitionResource,
            FhirConstants.OperationOutcomeResource,
        };

        public static IEnumerable<JObject> LoadNdjsonData(string filePath)
        {
            // Date formatted strings are not parsed to a date type and are read as strings,
            // to prevent output from being affected by time zone.
            var serializerSettings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };

            foreach (string line in File.ReadAllLines(filePath))
            {
                yield return JsonConvert.DeserializeObject<JObject>(line, serializerSettings);
            }
        }

        public static IContainerRegistryTemplateProvider GetMockAcrTemplateProvider()
        {
            Dictionary<string, byte[]> templateContents = Directory.GetFiles(TestCustomizedSchemaDirectoryPath, "*", SearchOption.AllDirectories)
                .Select(filePath =>
                {
                    byte[] templateContent = File.ReadAllBytes(filePath);
                    return new KeyValuePair<string, byte[]>(Path.GetRelativePath(TestCustomizedSchemaDirectoryPath, filePath), templateContent);
                }).ToDictionary(x => x.Key, x => x.Value);

            Dictionary<string, Template> templateCollection = TemplateLayerParser.ParseToTemplates(templateContents);

            var templateProvider = Substitute.For<IContainerRegistryTemplateProvider>();
            templateProvider.GetTemplateCollectionAsync(default, default).ReturnsForAnyArgs(new List<Dictionary<string, Template>> { templateCollection });
            return templateProvider;
        }

        public static IParquetSchemaProvider TestFhirParquetSchemaProviderDelegate(string name)
        {
            if (name == ParquetSchemaConstants.DefaultSchemaProviderKey)
            {
                return new LocalDefaultSchemaProvider(
                    Options.Create(new DataSourceConfiguration()),
                    _diagnosticLogger,
                    NullLogger<LocalDefaultSchemaProvider>.Instance);
            }
            else
            {
                return new AcrCustomizedSchemaProvider(
                    GetMockAcrTemplateProvider(),
                    Options.Create(TestCustomSchemaConfiguration),
                    _diagnosticLogger,
                    NullLogger<AcrCustomizedSchemaProvider>.Instance);
            }
        }

        public static IParquetSchemaProvider TestDicomParquetSchemaProviderDelegate(string name)
        {
            if (name == ParquetSchemaConstants.DefaultSchemaProviderKey)
            {
                return new LocalDefaultSchemaProvider(
                    Options.Create(new DataSourceConfiguration { Type = DataSourceType.DICOM }),
                    _diagnosticLogger,
                    NullLogger<LocalDefaultSchemaProvider>.Instance);
            }
            else
            {
                return new AcrCustomizedSchemaProvider(
                    GetMockAcrTemplateProvider(),
                    Options.Create(TestCustomSchemaConfiguration),
                    _diagnosticLogger,
                    NullLogger<AcrCustomizedSchemaProvider>.Instance);
            }
        }

        public static IDataSchemaConverter TestDataSchemaConverterDelegate(string name)
        {
            var schemaManagerWithoutCustomizedSchema = new ParquetSchemaManager(
                Options.Create(new SchemaConfiguration()),
                TestFhirParquetSchemaProviderDelegate,
                _diagnosticLogger,
                NullLogger<ParquetSchemaManager>.Instance);

            if (name == ParquetSchemaConstants.DefaultSchemaProviderKey)
            {
                return new FhirDefaultSchemaConverter(
                    schemaManagerWithoutCustomizedSchema,
                    _diagnosticLogger,
                    NullLogger<FhirDefaultSchemaConverter>.Instance);
            }
            else
            {
                return new CustomSchemaConverter(
                    GetMockAcrTemplateProvider(),
                    Options.Create(TestCustomSchemaConfiguration),
                    _diagnosticLogger,
                    NullLogger<CustomSchemaConverter>.Instance);
            }
        }

        public static IDataSchemaConverter TestDicomDataSchemaConverterDelegate(string name)
        {
            var schemaManagerWithoutCustomizedSchema = new ParquetSchemaManager(
                Options.Create(new SchemaConfiguration()),
                TestDicomParquetSchemaProviderDelegate,
                _diagnosticLogger,
                NullLogger<ParquetSchemaManager>.Instance);

            if (name == ParquetSchemaConstants.DefaultSchemaProviderKey)
            {
                return new DicomDefaultSchemaConverter(
                    schemaManagerWithoutCustomizedSchema,
                    _diagnosticLogger,
                    NullLogger<DicomDefaultSchemaConverter>.Instance);
            }
            else
            {
                return new CustomSchemaConverter(
                    GetMockAcrTemplateProvider(),
                    Options.Create(TestCustomSchemaConfiguration),
                    _diagnosticLogger,
                    NullLogger<CustomSchemaConverter>.Instance);
            }
        }
    }
}
