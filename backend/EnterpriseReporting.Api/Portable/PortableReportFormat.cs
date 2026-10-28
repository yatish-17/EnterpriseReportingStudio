using System.Globalization;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EnterpriseReporting.Application.DataSources;
using EnterpriseReporting.Application.Reports;

namespace EnterpriseReporting.Api.Portable;

public static partial class PortableReportFormat
{
    public const string PackageFormat = "ers-portable-report";
    public const int PackageVersion = 1;
    private static readonly XNamespace RdlNamespace =
        "http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition";
    private static readonly XNamespace RdlDesignerNamespace =
        "http://schemas.microsoft.com/SQLServer/reporting/reportdesigner";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Validate(PortableReportPackage package)
    {
        if (package.Datasets is null || package.Limitations is null ||
            package.Definition.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new PortableReportException("The report package is missing its dataset list or report definition.");
        }

        if (package.Format != PackageFormat || package.Version != PackageVersion)
        {
            throw new PortableReportException(
                $"This report package format is not supported (expected {PackageFormat} version {PackageVersion}).");
        }

        if (string.IsNullOrWhiteSpace(package.Name) || package.Name.Length > 250)
        {
            throw new PortableReportException("The package must include a report name of 1 to 250 characters.");
        }

        if (package.ReportType is not ("SingleSource" or "MultiSource"))
        {
            throw new PortableReportException("Only single-source and combined table reports are supported.");
        }

        if (package.Datasets.Count is < 1 or > 150 ||
            package.Datasets.Any(dataset =>
                dataset is null ||
                dataset.OriginalDatasetId < 1 ||
                string.IsNullOrWhiteSpace(dataset.Alias) ||
                string.IsNullOrWhiteSpace(dataset.Name) ||
                string.IsNullOrWhiteSpace(dataset.QueryDefinition) ||
                dataset.Columns is null ||
                dataset.Columns.Count > 50 ||
                dataset.Columns.Any(column =>
                    column is null || string.IsNullOrWhiteSpace(column.Name) ||
                    string.IsNullOrWhiteSpace(column.DataType)) ||
                (dataset.RdlConnectionSettings is { } connectionSettings &&
                    (string.IsNullOrWhiteSpace(connectionSettings.ServerAddress) ||
                     string.IsNullOrWhiteSpace(connectionSettings.DatabaseName) ||
                     connectionSettings.AuthenticationType is not ("Integrated" or "SqlPassword"))) ||
                (dataset.Snapshot is { } snapshot &&
                    (snapshot.Columns is null || snapshot.Columns.Count is < 1 or > 50 ||
                     snapshot.Rows is null || snapshot.Rows.Count > 5_000 ||
                     snapshot.Rows.Any(row => row is null || row.Count != snapshot.Columns.Count) ||
                     snapshot.QueryDefinition != dataset.QueryDefinition))))
        {
            throw new PortableReportException("The package contains an invalid dataset definition.");
        }

        if (package.Datasets.Select(dataset => dataset.OriginalDatasetId).Distinct().Count() !=
            package.Datasets.Count)
        {
            throw new PortableReportException("The package contains duplicate dataset identifiers.");
        }

        if (package.ReportType == "SingleSource")
        {
            var definition = package.Definition.Deserialize<PortableSingleReportDefinition>(JsonOptions);
            if (definition?.Design is null || definition.Design.Fields is null ||
                definition.Design.Fields.Count is < 1 or > 50 ||
                definition.Parameters is null ||
                definition.Parameters.Any(parameter =>
                    parameter is null || !IsValidRdlParameterName(parameter.Name)) ||
                definition.Parameters.Select(parameter => parameter.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != definition.Parameters.Count ||
                definition.Design.Fields.Any(field =>
                    field is null || field.ColumnIndex < 0 || field.Width < 1 ||
                    string.IsNullOrWhiteSpace(field.ColumnName) ||
                    string.IsNullOrWhiteSpace(field.DisplayName)) ||
                definition.Design.IncludeTable == false ||
                (definition.Design.Charts?.Count ?? 0) > 0 || definition.Design.Chart is not null)
            {
                throw new PortableReportException("This package is not a supported table-only report.");
            }

            if (package.Datasets.Count != 1)
            {
                throw new PortableReportException("A single-source report package must contain exactly one dataset.");
            }

            return;
        }

        var combined = package.Definition.Deserialize<MultiSourceDesign>(JsonOptions);
        if (combined is null || combined.Components is null || combined.Datasets is null ||
            combined.Relationships is null || combined.Parameters is null ||
            combined.Datasets.Count is < 2 or > 150 ||
            combined.Components.Count is < 1 or > 150 ||
            combined.Components.Any(component =>
                component is null ||
                !string.Equals(component.Type, "Table", StringComparison.OrdinalIgnoreCase) ||
                component.Fields is null || component.Fields.Count is < 1 or > 50 ||
                component.Fields.Any(field =>
                    field is null || string.IsNullOrWhiteSpace(field.Alias) ||
                    string.IsNullOrWhiteSpace(field.ColumnName) ||
                    string.IsNullOrWhiteSpace(field.DisplayName))) ||
            combined.Relationships.Any(relationship =>
                relationship is null ||
                string.IsNullOrWhiteSpace(relationship.LeftAlias) ||
                string.IsNullOrWhiteSpace(relationship.LeftColumn) ||
                string.IsNullOrWhiteSpace(relationship.RightAlias) ||
                string.IsNullOrWhiteSpace(relationship.RightColumn)) ||
            combined.Parameters.Any(parameter =>
                parameter is null || !IsValidRdlParameterName(parameter.Name)) ||
            combined.Parameters.Select(parameter => parameter.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != combined.Parameters.Count)
        {
            throw new PortableReportException("Combined packages may contain tables only.");
        }

        var includedIds = package.Datasets.Select(dataset => dataset.OriginalDatasetId).ToHashSet();
        if (combined.Datasets.Any(dataset => dataset is null || !includedIds.Contains(dataset.DatasetId)))
        {
            throw new PortableReportException("The package is missing a dataset required by the combined report.");
        }

        if (combined.Datasets.Select(dataset => dataset.Alias)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != combined.Datasets.Count ||
            combined.Datasets.Count != package.Datasets.Count ||
            combined.Datasets.Any(dataset => package.Datasets.SingleOrDefault(
                item => item.OriginalDatasetId == dataset.DatasetId)?.Alias != dataset.Alias))
        {
            throw new PortableReportException("The combined report dataset aliases do not match the packaged datasets.");
        }
    }

    public static PortableReportPackage ParsePackage(
        Stream input,
        string reportName,
        string? category = null)
    {
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var content = buffer.ToArray();
        var firstCharacter = content.FirstOrDefault(value => !char.IsWhiteSpace((char)value));
        if (firstCharacter == (byte)'<')
        {
            using var xmlStream = new MemoryStream(content, writable: false);
            using var xmlReader = XmlReader.Create(xmlStream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 100_000_000,
            });
            return ParseRdl(XDocument.Load(xmlReader), reportName, category);
        }

        using var document = JsonDocument.Parse(content, new JsonDocumentOptions
        {
            MaxDepth = 64,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("format", out _))
        {
            var package = root.Deserialize<PortableReportPackage>(JsonOptions)
                ?? throw new PortableReportException("The uploaded package is empty.");
            Validate(package);
            return package;
        }

        throw new PortableReportException("The uploaded file is neither a valid .ersreport package nor Microsoft RDL.");
    }

    public static byte[] CreateRdl(PortableReportPackage package)
    {
        Validate(package);
        if (package.ReportType == "MultiSource")
        {
            return CreateMultiSourceRdl(package);
        }

        var definition = package.Definition.Deserialize<PortableSingleReportDefinition>(JsonOptions)
            ?? throw new PortableReportException("The report definition is invalid.");
        if (definition.Parameters.Any(parameter => parameter.AllowMultipleValues))
        {
            throw new PortableReportException("RDL export does not currently support multi-value report parameters.");
        }

        if (!string.IsNullOrWhiteSpace(definition.Design.SortBy))
        {
            throw new PortableReportException("RDL export does not currently support report-level sorting.");
        }

        var dataset = package.Datasets[0];
        var fieldsByIndex = dataset.Columns
            .Select((column, index) => (column, index))
            .ToDictionary(item => item.index, item => item.column);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = definition.Design.Fields.Select(field =>
        {
            if (!fieldsByIndex.TryGetValue(field.ColumnIndex, out var column) ||
                !string.Equals(field.ColumnName, column.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new PortableReportException(
                    $"The report field '{field.DisplayName}' no longer matches its dataset schema.");
            }

            var fieldName = UniqueRdlName(field.ColumnName, usedNames);
            return (Field: field, Column: column, Name: fieldName);
        }).ToArray();
        var reportParameterNames = definition.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queryParameterNames = SqlParameterRegex().Matches(dataset.QueryDefinition)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!queryParameterNames.IsSubsetOf(reportParameterNames))
        {
            throw new PortableReportException("The dataset query has parameters that are missing from the report definition.");
        }

        var tablix = new XElement(RdlNamespace + "Tablix",
            new XAttribute("Name", "ReportTable"),
            new XElement(RdlNamespace + "TablixBody",
                new XElement(RdlNamespace + "TablixColumns",
                    fields.Select(item => new XElement(RdlNamespace + "TablixColumn",
                        new XElement(RdlNamespace + "Width",
                            $"{Math.Max(0.5, item.Field.Width / 96d).ToString("0.##", CultureInfo.InvariantCulture)}in")))),
                new XElement(RdlNamespace + "TablixRows",
                    CreateRdlRow(fields.Select(item => item.Field.DisplayName), header: true),
                    CreateRdlRow(fields.Select(item => $"=Fields!{item.Name}.Value"), header: false))),
            new XElement(RdlNamespace + "TablixColumnHierarchy",
                new XElement(RdlNamespace + "TablixMembers",
                    fields.Select(_ => new XElement(RdlNamespace + "TablixMember")))),
            new XElement(RdlNamespace + "TablixRowHierarchy",
                new XElement(RdlNamespace + "TablixMembers",
                    new XElement(RdlNamespace + "TablixMember",
                        new XElement(RdlNamespace + "KeepWithGroup", "After"),
                        new XElement(RdlNamespace + "RepeatOnNewPage", "true")),
                    new XElement(RdlNamespace + "TablixMember",
                        new XElement(RdlNamespace + "Group", new XAttribute("Name", "Details"))))),
            new XElement(RdlNamespace + "DataSetName", "ReportData"),
            new XElement(RdlNamespace + "Top", "0.2in"),
            new XElement(RdlNamespace + "Left", "0.2in"),
            new XElement(RdlNamespace + "Height", "0.5in"),
            new XElement(RdlNamespace + "Width",
                $"{fields.Sum(item => Math.Max(0.5, item.Field.Width / 96d)).ToString("0.##", CultureInfo.InvariantCulture)}in"));

        var report = new XElement(RdlNamespace + "Report",
            new XAttribute(XNamespace.Xmlns + "rd", RdlDesignerNamespace),
            new XElement(RdlNamespace + "AutoRefresh", "0"),
            new XElement(RdlNamespace + "DataSources",
                CreateRdlDataSource(dataset, "ReportDataSource")),
            new XElement(RdlNamespace + "DataSets",
                new XElement(RdlNamespace + "DataSet",
                    new XAttribute("Name", "ReportData"),
                    new XElement(RdlNamespace + "Query",
                        new XElement(RdlNamespace + "DataSourceName", "ReportDataSource"),
                        new XElement(RdlNamespace + "CommandText", dataset.QueryDefinition),
                        new XElement(RdlNamespace + "QueryParameters",
                            definition.Parameters.Select(parameter =>
                                new XElement(RdlNamespace + "QueryParameter",
                                    new XAttribute("Name", $"@{parameter.Name}"),
                                    new XElement(RdlNamespace + "Value", $"=Parameters!{parameter.Name}.Value"))))),
                    new XElement(RdlNamespace + "Fields",
                        fields.Select(item =>
                            new XElement(RdlNamespace + "Field",
                                new XAttribute("Name", item.Name),
                                new XElement(RdlNamespace + "DataField", item.Column.Name),
                                new XElement(RdlDesignerNamespace + "TypeName", RdlClrType(item.Column.DataType))))))),
            new XElement(RdlNamespace + "ReportSections",
                new XElement(RdlNamespace + "ReportSection",
                    new XElement(RdlNamespace + "Body",
                        new XElement(RdlNamespace + "ReportItems", tablix),
                        new XElement(RdlNamespace + "Height", "2in")),
                    new XElement(RdlNamespace + "Width", "8.5in"),
                    new XElement(RdlNamespace + "Page",
                        new XElement(RdlNamespace + "PageHeight", "11in"),
                        new XElement(RdlNamespace + "PageWidth", "8.5in"),
                        new XElement(RdlNamespace + "LeftMargin", "0.5in"),
                        new XElement(RdlNamespace + "RightMargin", "0.5in"),
                        new XElement(RdlNamespace + "TopMargin", "0.5in"),
                        new XElement(RdlNamespace + "BottomMargin", "0.5in")))),
            new XElement(RdlNamespace + "ReportParameters",
                definition.Parameters.Select(parameter => new XElement(RdlNamespace + "ReportParameter",
                    new XAttribute("Name", parameter.Name),
                    new XElement(RdlNamespace + "DataType", RdlDataType(parameter.DataType)),
                    new XElement(RdlNamespace + "Prompt", parameter.DisplayName),
                    parameter.DefaultValue is null ? null :
                        new XElement(RdlNamespace + "DefaultValue",
                            new XElement(RdlNamespace + "Values",
                                new XElement(RdlNamespace + "Value", parameter.DefaultValue)))))),
            CreateRdlParameterLayout(definition.Parameters),
            new XElement(RdlNamespace + "Language", "en-US"));

        return System.Text.Encoding.UTF8.GetBytes(
            new XDocument(new XDeclaration("1.0", "utf-8", null), report)
                .ToString(SaveOptions.DisableFormatting));
    }

    private static byte[] CreateMultiSourceRdl(PortableReportPackage package)
    {
        var definition = package.Definition.Deserialize<MultiSourceDesign>(JsonOptions)
            ?? throw new PortableReportException("The combined report definition is invalid.");
        if (definition.Components.Count != 1)
        {
            throw new PortableReportException(
                "RDL export currently supports one table component in a combined report.");
        }

        var component = definition.Components[0];
        if (component.Fields.Count == 0 || component.SortField is not null ||
            component.CategoryField is not null || component.ValueField is not null ||
            definition.Parameters.Any(parameter => parameter.AllowMultipleValues))
        {
            throw new PortableReportException(
                "RDL export requires table fields, scalar parameters, and no component-level sorting.");
        }
        if (definition.Relationships.Any(relationship =>
                !string.Equals(relationship.JoinType, "Inner", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(relationship.JoinType, "Left", StringComparison.OrdinalIgnoreCase)))
        {
            throw new PortableReportException(
                "RDL export supports only inner and left combined-dataset relationships.");
        }

        var baseAlias = definition.Datasets[0].Alias;
        var definitionsByAlias = package.Datasets.ToDictionary(
            dataset => dataset.Alias, StringComparer.OrdinalIgnoreCase);
        var reportParameterNames = definition.Parameters
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var datasetNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dataSourceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fieldNames = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var usedDatasetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedDataSourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dataset in definition.Datasets)
        {
            var packagedDataset = definitionsByAlias[dataset.Alias];
            datasetNames.Add(dataset.Alias, UniqueRdlName($"DataSet_{dataset.Alias}", usedDatasetNames));
            dataSourceNames.Add(dataset.Alias,
                UniqueRdlName($"DataSource_{dataset.Alias}_{packagedDataset.SourceName}", usedDataSourceNames));

            var usedFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentFieldNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in packagedDataset.Columns)
            {
                if (!currentFieldNames.TryAdd(column.Name, UniqueRdlName(column.Name, usedFieldNames)))
                {
                    throw new PortableReportException(
                        $"Dataset '{dataset.Alias}' has duplicate column names that RDL cannot distinguish.");
                }
            }

            fieldNames.Add(dataset.Alias, currentFieldNames);

            var queryParameters = SqlParameterRegex().Matches(packagedDataset.QueryDefinition)
                .Select(match => match.Groups[1].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!queryParameters.IsSubsetOf(reportParameterNames))
            {
                throw new PortableReportException(
                    $"Dataset '{dataset.Alias}' has query parameters missing from the report definition.");
            }
        }

        var tablixFields = component.Fields.Select(field =>
        {
            if (!definitionsByAlias.TryGetValue(field.Alias, out var dataset) ||
                !fieldNames[field.Alias].TryGetValue(field.ColumnName, out var rdlFieldName))
            {
                throw new PortableReportException(
                    $"The table refers to unavailable field '{field.Alias}.{field.ColumnName}'.");
            }

            if (!string.Equals(field.Alias, baseAlias, StringComparison.OrdinalIgnoreCase))
            {
                var relationships = definition.Relationships.Where(relationship =>
                    (string.Equals(relationship.LeftAlias, baseAlias, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(relationship.RightAlias, field.Alias, StringComparison.OrdinalIgnoreCase)) ||
                    (string.Equals(relationship.RightAlias, baseAlias, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(relationship.LeftAlias, field.Alias, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (relationships.Length != 1)
                {
                    throw new PortableReportException(
                        $"RDL export requires exactly one direct relationship from '{baseAlias}' to '{field.Alias}'.");
                }
            }

            var expression = string.Equals(field.Alias, baseAlias, StringComparison.OrdinalIgnoreCase)
                ? $"=Fields!{rdlFieldName}.Value"
                : CreateRdlLookupExpression(field, baseAlias, definition.Relationships,
                    definitionsByAlias, fieldNames, datasetNames);
            return (Field: field, Dataset: dataset, Name: rdlFieldName, Expression: expression);
        }).ToArray();

        var tablix = new XElement(RdlNamespace + "Tablix",
            new XAttribute("Name", "ReportTable"),
            new XElement(RdlNamespace + "TablixBody",
                new XElement(RdlNamespace + "TablixColumns",
                    tablixFields.Select(_ => new XElement(RdlNamespace + "TablixColumn",
                        new XElement(RdlNamespace + "Width", "1in")))),
                new XElement(RdlNamespace + "TablixRows",
                    CreateRdlRow(tablixFields.Select(item => item.Field.DisplayName), header: true),
                    CreateRdlRow(tablixFields.Select(item => item.Expression), header: false))),
            new XElement(RdlNamespace + "TablixColumnHierarchy",
                new XElement(RdlNamespace + "TablixMembers",
                    tablixFields.Select(_ => new XElement(RdlNamespace + "TablixMember")))),
            new XElement(RdlNamespace + "TablixRowHierarchy",
                new XElement(RdlNamespace + "TablixMembers",
                    new XElement(RdlNamespace + "TablixMember",
                        new XElement(RdlNamespace + "KeepWithGroup", "After"),
                        new XElement(RdlNamespace + "RepeatOnNewPage", "true")),
                    new XElement(RdlNamespace + "TablixMember",
                        new XElement(RdlNamespace + "Group", new XAttribute("Name", "Details"))))),
            new XElement(RdlNamespace + "DataSetName", datasetNames[baseAlias]),
            new XElement(RdlNamespace + "Top", "0.2in"),
            new XElement(RdlNamespace + "Left", "0.2in"),
            new XElement(RdlNamespace + "Height", "0.5in"),
            new XElement(RdlNamespace + "Width",
                $"{tablixFields.Length.ToString(CultureInfo.InvariantCulture)}in"));

        var rdlDataSets = definition.Datasets.Select(dataset =>
        {
            var packagedDataset = definitionsByAlias[dataset.Alias];
            var queryParameters = SqlParameterRegex().Matches(packagedDataset.QueryDefinition)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return new XElement(RdlNamespace + "DataSet",
                new XAttribute("Name", datasetNames[dataset.Alias]),
                new XElement(RdlNamespace + "Query",
                    new XElement(RdlNamespace + "DataSourceName", dataSourceNames[dataset.Alias]),
                    new XElement(RdlNamespace + "CommandText", packagedDataset.QueryDefinition),
                    new XElement(RdlNamespace + "QueryParameters",
                        queryParameters.Select(parameter =>
                            new XElement(RdlNamespace + "QueryParameter",
                                new XAttribute("Name", $"@{parameter}"),
                                new XElement(RdlNamespace + "Value", $"=Parameters!{parameter}.Value"))))),
                new XElement(RdlNamespace + "Fields",
                    packagedDataset.Columns.Select(column =>
                        new XElement(RdlNamespace + "Field",
                            new XAttribute("Name", fieldNames[dataset.Alias][column.Name]),
                            new XElement(RdlNamespace + "DataField", column.Name),
                            new XElement(RdlDesignerNamespace + "TypeName",
                                RdlClrType(column.DataType))))));
        }).ToArray();

        var report = new XElement(RdlNamespace + "Report",
            new XAttribute(XNamespace.Xmlns + "rd", RdlDesignerNamespace),
            new XElement(RdlNamespace + "AutoRefresh", "0"),
            new XElement(RdlNamespace + "DataSources",
                definition.Datasets.Select(dataset =>
                    CreateRdlDataSource(
                        definitionsByAlias[dataset.Alias],
                        dataSourceNames[dataset.Alias]))),
            new XElement(RdlNamespace + "DataSets", rdlDataSets),
            new XElement(RdlNamespace + "ReportSections",
                new XElement(RdlNamespace + "ReportSection",
                    new XElement(RdlNamespace + "Body",
                        new XElement(RdlNamespace + "ReportItems", tablix),
                        new XElement(RdlNamespace + "Height", "2in")),
                    new XElement(RdlNamespace + "Width", "8.5in"),
                    new XElement(RdlNamespace + "Page",
                        new XElement(RdlNamespace + "PageHeight", "11in"),
                        new XElement(RdlNamespace + "PageWidth", "8.5in"),
                        new XElement(RdlNamespace + "LeftMargin", "0.5in"),
                        new XElement(RdlNamespace + "RightMargin", "0.5in"),
                        new XElement(RdlNamespace + "TopMargin", "0.5in"),
                        new XElement(RdlNamespace + "BottomMargin", "0.5in")))),
            new XElement(RdlNamespace + "ReportParameters",
                definition.Parameters.Select(parameter => new XElement(RdlNamespace + "ReportParameter",
                    new XAttribute("Name", parameter.Name),
                    new XElement(RdlNamespace + "DataType", RdlDataType(parameter.DataType)),
                    new XElement(RdlNamespace + "Prompt", parameter.DisplayName),
                    parameter.DefaultValue is null ? null :
                        new XElement(RdlNamespace + "DefaultValue",
                            new XElement(RdlNamespace + "Values",
                                new XElement(RdlNamespace + "Value", parameter.DefaultValue)))))),
            CreateRdlParameterLayout(definition.Parameters),
            new XElement(RdlNamespace + "Language", "en-US"));

        return System.Text.Encoding.UTF8.GetBytes(
            new XDocument(new XDeclaration("1.0", "utf-8", null), report)
                .ToString(SaveOptions.DisableFormatting));
    }

    private static string CreateRdlLookupExpression(
        MultiSourceField field,
        string baseAlias,
        IReadOnlyList<MultiSourceRelationship> relationships,
        IReadOnlyDictionary<string, PortableDatasetDefinition> datasetsByAlias,
        IReadOnlyDictionary<string, Dictionary<string, string>> fieldNames,
        IReadOnlyDictionary<string, string> datasetNames)
    {
        var relationship = relationships.Single(item =>
            string.Equals(item.LeftAlias, baseAlias, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.RightAlias, field.Alias, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.RightAlias, baseAlias, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.LeftAlias, field.Alias, StringComparison.OrdinalIgnoreCase));
        var sourceKey = string.Equals(relationship.LeftAlias, baseAlias, StringComparison.OrdinalIgnoreCase)
            ? relationship.LeftColumn
            : relationship.RightColumn;
        var destinationKey = string.Equals(relationship.LeftAlias, baseAlias, StringComparison.OrdinalIgnoreCase)
            ? relationship.RightColumn
            : relationship.LeftColumn;
        if (!fieldNames[baseAlias].TryGetValue(sourceKey, out var sourceKeyName) ||
            !fieldNames[field.Alias].TryGetValue(destinationKey, out var destinationKeyName) ||
            !datasetsByAlias.ContainsKey(field.Alias))
        {
            throw new PortableReportException(
                $"The relationship between '{baseAlias}' and '{field.Alias}' refers to a missing join column.");
        }

        var resultFieldName = fieldNames[field.Alias][field.ColumnName];
        return $"=Lookup(Fields!{sourceKeyName}.Value, Fields!{destinationKeyName}.Value, " +
               $"Fields!{resultFieldName}.Value, \"{datasetNames[field.Alias]}\")";
    }

    private static XElement CreateRdlDataSource(PortableDatasetDefinition dataset, string name)
    {
        var settings = dataset.RdlConnectionSettings;
        var connectionString = "Data Source=YOUR_SQL_SERVER;Initial Catalog=YOUR_DATABASE";
        var integratedSecurity = false;
        if (settings is not null)
        {
            var builder = new DbConnectionStringBuilder
            {
                ["Data Source"] = settings.ServerAddress,
                ["Initial Catalog"] = settings.DatabaseName,
                ["Encrypt"] = settings.EncryptConnection,
                ["TrustServerCertificate"] = settings.TrustServerCertificate,
            };
            connectionString = builder.ConnectionString;
            integratedSecurity = string.Equals(
                settings.AuthenticationType, "Integrated", StringComparison.Ordinal);
        }

        return new XElement(RdlNamespace + "DataSource",
            new XAttribute("Name", name),
            new XElement(RdlNamespace + "ConnectionProperties",
                new XElement(RdlNamespace + "DataProvider", "SQL"),
                new XElement(RdlNamespace + "ConnectString", connectionString),
                new XElement(RdlNamespace + "IntegratedSecurity",
                    integratedSecurity ? "true" : "false")),
            new XElement(RdlDesignerNamespace + "SecurityType",
                integratedSecurity ? "Integrated" : "None"));
    }

    private static PortableReportPackage ParseRdl(
        XDocument document,
        string reportName,
        string? category)
    {
        var root = document.Root
            ?? throw new PortableReportException("The RDL document is empty.");
        if (root.Name.LocalName != "Report")
        {
            throw new PortableReportException("The uploaded XML is not a Microsoft Report Definition Language report.");
        }

        if (root.Descendants().Any(element => element.Name.LocalName == "SortExpressions"))
        {
            throw new PortableReportException("RDL import does not currently support sort expressions.");
        }

        var datasets = root.Descendants().Where(element => element.Name.LocalName == "DataSet").ToArray();
        if (datasets.Length != 1)
        {
            throw new PortableReportException("RDL import supports reports with exactly one dataset.");
        }

        var datasetElement = datasets[0];
        var query = datasetElement.Elements().FirstOrDefault(element => element.Name.LocalName == "Query");
        var queryText = query?.Elements().FirstOrDefault(element => element.Name.LocalName == "CommandText")?.Value;
        if (string.IsNullOrWhiteSpace(queryText))
        {
            throw new PortableReportException("The RDL dataset does not contain a SQL query.");
        }

        var datasetFields = datasetElement.Descendants()
            .Where(element => element.Name.LocalName == "Field")
            .Select(field => new
            {
                Name = (string?)field.Attribute("Name"),
                DataField = field.Elements().FirstOrDefault(element => element.Name.LocalName == "DataField")?.Value,
                TypeName = SqlTypeFromRdlTypeName(
                    field.Descendants().FirstOrDefault(element => element.Name.LocalName == "TypeName")?.Value ?? "String"),
            })
            .Where(field => !string.IsNullOrWhiteSpace(field.Name))
            .ToArray();
        if (datasetFields.Length == 0)
        {
            throw new PortableReportException("The RDL dataset does not define any fields.");
        }

        var tablixes = root.Descendants().Where(element => element.Name.LocalName == "Tablix").ToArray();
        if (tablixes.Length != 1)
        {
            throw new PortableReportException("RDL import supports exactly one table (Tablix) and no other report parts.");
        }

        var tablixRows = tablixes[0].Descendants()
            .Where(element => element.Name.LocalName == "TablixRow")
            .Select(row => row.Descendants()
                .Where(element => element.Name.LocalName == "TablixCell")
                .Select(cell => cell.Descendants().FirstOrDefault(element => element.Name.LocalName == "Textbox")
                    ?.Descendants().FirstOrDefault(element => element.Name.LocalName == "Value")?.Value ?? string.Empty)
                .ToArray())
            .Where(row => row.Length > 0)
            .ToArray();
        var detailRowIndex = Array.FindIndex(tablixRows, row => row.Any(value => RdlFieldExpressionRegex().IsMatch(value)));
        if (detailRowIndex < 0)
        {
            throw new PortableReportException("The RDL table has no supported =Fields!Name.Value detail expressions.");
        }

        var detailValues = tablixRows[detailRowIndex];
        var headerValues = detailRowIndex > 0 ? tablixRows[detailRowIndex - 1] : [];
        var tableColumns = tablixes[0].Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "TablixColumns")?
            .Elements().Where(element => element.Name.LocalName == "TablixColumn").ToArray() ?? [];
        var columns = new List<SqlColumnInfo>();
        var reportFields = new List<ReportField>();
        for (var index = 0; index < detailValues.Length; index++)
        {
            var expression = RdlFieldExpressionRegex().Match(detailValues[index]);
            if (!expression.Success)
            {
                throw new PortableReportException(
                    "The RDL table contains a calculated or unsupported detail expression.");
            }

            var field = datasetFields.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, expression.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            if (field is null || string.IsNullOrWhiteSpace(field.DataField))
            {
                throw new PortableReportException(
                    $"The RDL table refers to an unavailable dataset field '{expression.Groups[1].Value}'.");
            }

            var column = new SqlColumnInfo(field.DataField, field.TypeName, true, false);
            columns.Add(column);
            var header = index < headerValues.Length
                ? headerValues[index].Trim()
                : string.Empty;
            if (header.StartsWith('=') || string.IsNullOrWhiteSpace(header))
            {
                header = field.DataField;
            }

            var width = index < tableColumns.Length
                ? ParseRdlWidth(tableColumns[index].Elements()
                    .FirstOrDefault(element => element.Name.LocalName == "Width")?.Value)
                : 120;
            reportFields.Add(new ReportField(index, column.Name, header, width));
        }

        var parameterElements = root.Descendants()
            .Where(element => element.Name.LocalName == "ReportParameter")
            .ToArray();
        var reportParameters = parameterElements.Select(element =>
        {
            var name = (string?)element.Attribute("Name");
            if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]{0,127}$"))
            {
                throw new PortableReportException("The RDL contains an invalid report parameter name.");
            }

            var dataType = element.Elements().FirstOrDefault(child => child.Name.LocalName == "DataType")?.Value switch
            {
                "Integer" => "Integer",
                "Float" => "Decimal",
                "Boolean" => "Boolean",
                "DateTime" => "DateTime",
                _ => "String",
            };
            var defaultValue = element.Descendants()
                .FirstOrDefault(child => child.Name.LocalName == "DefaultValue")?
                .Descendants().FirstOrDefault(child => child.Name.LocalName == "Value")?.Value;
            var prompt = element.Elements().FirstOrDefault(child => child.Name.LocalName == "Prompt")?.Value;
            return new ReportParameter(name, string.IsNullOrWhiteSpace(prompt) ? name : prompt,
                dataType, defaultValue, defaultValue is null, false);
        }).ToArray();
        var design = new ReportDesign(
            reportName,
            reportFields,
            null,
            false,
            IncludeTable: true,
            Charts: [],
            ComponentOrder: [-1]);
        var definition = new PortableSingleReportDefinition(design, reportParameters);
        var package = new PortableReportPackage(
            PackageFormat,
            PackageVersion,
            "SingleSource",
            reportName,
            null,
            category,
            JsonSerializer.SerializeToElement(definition, JsonOptions),
            [
                new PortableDatasetDefinition(
                    1,
                    "Main",
                    reportName,
                    null,
                    string.Empty,
                    queryText,
                    datasetFields.Select(field => new SqlColumnInfo(
                        field.DataField ?? field.Name!, field.TypeName, true, false)).ToArray()),
            ],
            ["The RDL database connection string was not imported. Select a permitted SQL data source in this installation."]);
        Validate(package);
        return package;
    }

    private static int ParseRdlWidth(string? width)
    {
        if (string.IsNullOrWhiteSpace(width)) return 120;
        var match = Regex.Match(width.Trim(), @"^([0-9]+(?:\.[0-9]+)?)(in|cm|mm|pt|pc)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var amount))
        {
            return 120;
        }

        var pixels = amount * (match.Groups[2].Value.ToLowerInvariant() switch
        {
            "in" => 96d,
            "cm" => 96d / 2.54d,
            "mm" => 96d / 25.4d,
            "pt" => 96d / 72d,
            "pc" => 16d,
            _ => 120d,
        });
        return (int)Math.Clamp(Math.Round(pixels), 60, 800);
    }

    private static XElement CreateRdlRow(IEnumerable<string> values, bool header) =>
        new(RdlNamespace + "TablixRow",
            new XElement(RdlNamespace + "Height", header ? "0.28in" : "0.25in"),
            new XElement(RdlNamespace + "TablixCells",
                values.Select((value, index) =>
                    new XElement(RdlNamespace + "TablixCell",
                        new XElement(RdlNamespace + "CellContents",
                            new XElement(RdlNamespace + "Textbox",
                                new XAttribute("Name", $"{(header ? "Header" : "Value")}{index + 1}"),
                                new XElement(RdlNamespace + "CanGrow", "true"),
                                new XElement(RdlNamespace + "KeepTogether", "true"),
                                new XElement(RdlNamespace + "Paragraphs",
                                    new XElement(RdlNamespace + "Paragraph",
                                        new XElement(RdlNamespace + "TextRuns",
                                            new XElement(RdlNamespace + "TextRun",
                                                new XElement(RdlNamespace + "Value", value),
                                                new XElement(RdlNamespace + "Style",
                                                    header ? new XElement(RdlNamespace + "FontWeight", "Bold") : null))),
                                        new XElement(RdlNamespace + "Style"))),
                                new XElement(RdlNamespace + "Style",
                                    new XElement(RdlNamespace + "Border",
                                        new XElement(RdlNamespace + "Style", "Solid")),
                                    new XElement(RdlNamespace + "PaddingLeft", "4pt"),
                                    new XElement(RdlNamespace + "PaddingRight", "4pt"),
                                    new XElement(RdlNamespace + "PaddingTop", "2pt"),
                                    new XElement(RdlNamespace + "PaddingBottom", "2pt"))))))));

    private static XElement? CreateRdlParameterLayout(
        IReadOnlyList<ReportParameter> parameters) =>
        parameters.Count == 0
            ? null
            : new XElement(RdlNamespace + "ReportParametersLayout",
                new XElement(RdlNamespace + "GridLayoutDefinition",
                    new XElement(RdlNamespace + "NumberOfColumns", "1"),
                    new XElement(RdlNamespace + "NumberOfRows",
                        parameters.Count.ToString(CultureInfo.InvariantCulture)),
                    new XElement(RdlNamespace + "CellDefinitions",
                        parameters.Select((parameter, index) =>
                            new XElement(RdlNamespace + "CellDefinition",
                                new XElement(RdlNamespace + "ColumnIndex", "0"),
                                new XElement(RdlNamespace + "RowIndex",
                                    index.ToString(CultureInfo.InvariantCulture)),
                                new XElement(RdlNamespace + "ParameterName", parameter.Name))))));

    private static string RdlDataType(string dataType) => dataType switch
    {
        "Integer" => "Integer",
        "Decimal" => "Float",
        "Boolean" => "Boolean",
        "Date" or "DateTime" => "DateTime",
        _ => "String",
    };

    private static bool IsValidRdlParameterName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant);

    private static string RdlClrType(string dataType) => dataType.Trim().ToLowerInvariant() switch
    {
        "bit" => "System.Boolean",
        "tinyint" => "System.Byte",
        "smallint" => "System.Int16",
        "int" => "System.Int32",
        "bigint" => "System.Int64",
        "decimal" or "numeric" or "money" or "smallmoney" => "System.Decimal",
        "real" => "System.Single",
        "float" => "System.Double",
        "date" or "datetime" or "datetime2" or "smalldatetime" => "System.DateTime",
        "time" => "System.TimeSpan",
        "uniqueidentifier" => "System.Guid",
        "binary" or "varbinary" or "image" or "rowversion" or "timestamp" => "System.Byte[]",
        _ => "System.String",
    };

    private static string SqlTypeFromRdlTypeName(string typeName) => typeName.Trim().ToLowerInvariant() switch
    {
        "system.boolean" => "bit",
        "system.byte" => "tinyint",
        "system.int16" => "smallint",
        "system.int32" => "int",
        "system.int64" => "bigint",
        "system.decimal" => "decimal",
        "system.single" => "real",
        "system.double" => "float",
        "system.datetime" => "datetime2",
        "system.timespan" => "time",
        "system.guid" => "uniqueidentifier",
        "system.byte[]" => "varbinary",
        "system.string" => "nvarchar",
        _ => typeName,
    };

    private static string UniqueRdlName(string fieldName, HashSet<string> usedNames)
    {
        var candidate = Regex.Replace(fieldName, "[^A-Za-z0-9_]", "_");
        if (candidate.Length == 0 || !char.IsLetter(candidate[0]) && candidate[0] != '_')
        {
            candidate = $"Field_{candidate}";
        }

        var unique = candidate;
        var suffix = 2;
        while (!usedNames.Add(unique))
        {
            unique = $"{candidate}_{suffix++}";
        }

        return unique;
    }

    [GeneratedRegex(@"(?<!@)@([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex SqlParameterRegex();

    [GeneratedRegex(@"^=\s*Fields!([A-Za-z_][A-Za-z0-9_]*)\.Value\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RdlFieldExpressionRegex();
}

public sealed class PortableReportException(string message) : Exception(message);
