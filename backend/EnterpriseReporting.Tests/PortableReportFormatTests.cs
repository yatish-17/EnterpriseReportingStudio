using System.Text.Json;
using EnterpriseReporting.Api.Portable;
using EnterpriseReporting.Application.DataSources;
using EnterpriseReporting.Application.Reports;
using System.Xml.Linq;

namespace EnterpriseReporting.Tests;

public sealed class PortableReportFormatTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RdlExportCanBeImportedWithoutLosingTableFieldsOrParameters()
    {
        var definition = new PortableSingleReportDefinition(
            new ReportDesign(
                "Orders",
                [new ReportField(0, "OrderId", "Order number", 144)],
                null,
                false,
                IncludeTable: true,
                Charts: []),
            [new ReportParameter("CustomerId", "Customer ID", "Integer", "17", true, false)]);
        var package = new PortableReportPackage(
            PortableReportFormat.PackageFormat,
            PortableReportFormat.PackageVersion,
            "SingleSource",
            "Orders",
            null,
            null,
            JsonSerializer.SerializeToElement(definition, JsonOptions),
            [
                new PortableDatasetDefinition(
                    4,
                    "OrdersData",
                    "Orders dataset",
                    null,
                    "Sales",
                    "SELECT OrderId FROM Orders WHERE CustomerId = @CustomerId",
                    [new SqlColumnInfo("OrderId", "int", false, true)]),
            ],
            []);

        var rdl = PortableReportFormat.CreateRdl(package);
        var rdlDocument = XDocument.Load(new MemoryStream(rdl));
        var imported = PortableReportFormat.ParsePackage(
            new MemoryStream(rdl), "Orders");
        var importedDefinition = imported.Definition
            .Deserialize<PortableSingleReportDefinition>(JsonOptions);
        var rdlNamespace = rdlDocument.Root!.Name.Namespace;
        var designerNamespace = XNamespace.Get(
            "http://schemas.microsoft.com/SQLServer/reporting/reportdesigner");
        var tablix = rdlDocument.Descendants(rdlNamespace + "Tablix").Single();

        Assert.Equal("Orders", imported.Name);
        Assert.Equal("SELECT OrderId FROM Orders WHERE CustomerId = @CustomerId",
            imported.Datasets[0].QueryDefinition);
        Assert.Equal("int", imported.Datasets[0].Columns[0].DataType);
        Assert.Equal(
            ["TablixBody", "TablixColumnHierarchy", "TablixRowHierarchy", "DataSetName"],
            tablix.Elements().Take(4).Select(element => element.Name.LocalName));
        Assert.Equal("System.Int32",
            rdlDocument.Descendants(XNamespace.Get(
                    "http://schemas.microsoft.com/SQLServer/reporting/reportdesigner") + "TypeName")
                .Single().Value);
        Assert.Equal("false",
            rdlDocument.Descendants(rdlNamespace + "IntegratedSecurity").Single().Value);
        Assert.Equal("None",
            rdlDocument.Descendants(designerNamespace + "SecurityType").Single().Value);
        Assert.Equal(
            rdlDocument.Descendants(rdlNamespace + "ReportParameter").Count(),
            rdlDocument.Descendants(rdlNamespace + "CellDefinition").Count());
        Assert.Equal("CustomerId",
            rdlDocument.Descendants(rdlNamespace + "CellDefinition")
                .Single().Element(rdlNamespace + "ParameterName")!.Value);
        Assert.NotNull(importedDefinition);
        Assert.Equal("Order number", importedDefinition.Design.Fields[0].DisplayName);
        Assert.Equal("CustomerId", importedDefinition.Parameters[0].Name);
        Assert.Equal("17", importedDefinition.Parameters[0].DefaultValue);
    }

    [Fact]
    public void MultiSourceRdlExportPreservesDatasetsParametersAndRelatedTableFields()
    {
        var definition = new MultiSourceDesign(
            "MultiSource",
            "Portable revenue",
            [
                new MultiSourceDataset("source1", 4),
                new MultiSourceDataset("source2", 5),
            ],
            [
                new MultiSourceRelationship(
                    "source1", "RegionMonthKey", "source2", "RegionMonthKey", "Inner"),
            ],
            [
                new MultiSourceComponent(
                    "table-1",
                    "Table",
                    "Revenue by region and month",
                    [
                        new MultiSourceField("source1", "RegionMonthKey", "Region/month"),
                        new MultiSourceField("source2", "Revenue", "Revenue"),
                    ]),
            ],
            [new ReportParameter("StartDate", "Start date", "Date", "2025-01-01", true, false)]);
        var package = new PortableReportPackage(
            PortableReportFormat.PackageFormat,
            PortableReportFormat.PackageVersion,
            "MultiSource",
            "Portable revenue",
            null,
            null,
            JsonSerializer.SerializeToElement(definition, JsonOptions),
            [
                new PortableDatasetDefinition(
                    4,
                    "source1",
                    "Base dataset",
                    null,
                    "Base SQL",
                    "SELECT RegionMonthKey FROM MonthlyBase WHERE PeriodStart >= @StartDate",
                    [new SqlColumnInfo("RegionMonthKey", "nvarchar", false, false)],
                    RdlConnectionSettings: new PortableRdlConnectionSettings(
                        "base-sql.example.test", "MonthlyBase", "Integrated", true, true)),
                new PortableDatasetDefinition(
                    5,
                    "source2",
                    "Revenue dataset",
                    null,
                    "Revenue SQL",
                    "SELECT RegionMonthKey, Revenue FROM MonthlyRevenue WHERE PeriodStart >= @StartDate",
                    [
                        new SqlColumnInfo("RegionMonthKey", "nvarchar", false, false),
                        new SqlColumnInfo("Revenue", "decimal", false, false),
                    ],
                    RdlConnectionSettings: new PortableRdlConnectionSettings(
                        "revenue-sql.example.test", "MonthlyRevenue", "SqlPassword", true, false)),
            ],
            []);

        var document = XDocument.Load(new MemoryStream(PortableReportFormat.CreateRdl(package)));
        var rdlNamespace = document.Root!.Name.Namespace;
        var designerNamespace = XNamespace.Get(
            "http://schemas.microsoft.com/SQLServer/reporting/reportdesigner");
        var dataSources = document.Descendants(rdlNamespace + "DataSource").ToArray();
        var datasets = document.Descendants(rdlNamespace + "DataSet").ToArray();
        var tablix = document.Descendants(rdlNamespace + "Tablix").Single();
        var rowValues = tablix.Descendants(rdlNamespace + "TablixRow")
            .Last()
            .Descendants(rdlNamespace + "Value")
            .Select(value => value.Value)
            .ToArray();

        Assert.Equal(2, dataSources.Length);
        Assert.All(dataSources, source =>
            Assert.DoesNotContain("Password", source.Value, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            ["true", "false"],
            document.Descendants(rdlNamespace + "IntegratedSecurity").Select(element => element.Value));
        Assert.Equal(
            ["Integrated", "None"],
            dataSources.Select(source => source.Element(designerNamespace + "SecurityType")!.Value));
        Assert.Contains("base-sql.example.test", dataSources[0].Value);
        Assert.Contains("MonthlyBase", dataSources[0].Value);
        Assert.Contains("revenue-sql.example.test", dataSources[1].Value);
        Assert.Contains("MonthlyRevenue", dataSources[1].Value);
        Assert.Equal(2, datasets.Length);
        Assert.All(datasets, dataset =>
            Assert.Contains("@StartDate",
                dataset.Element(rdlNamespace + "Query")!.Value));
        Assert.Equal("DataSet_source1", tablix.Element(rdlNamespace + "DataSetName")!.Value);
        Assert.Contains("=Fields!RegionMonthKey.Value", rowValues);
        Assert.Contains(
            "=Lookup(Fields!RegionMonthKey.Value, Fields!RegionMonthKey.Value, Fields!Revenue.Value, \"DataSet_source2\")",
            rowValues);
        Assert.Contains(document.Descendants(rdlNamespace + "ReportParameter"),
            parameter => (string?)parameter.Attribute("Name") == "StartDate");
        Assert.Equal(
            document.Descendants(rdlNamespace + "ReportParameter").Count(),
            document.Descendants(rdlNamespace + "CellDefinition").Count());
        Assert.Equal(
            document.Descendants(rdlNamespace + "ReportParameter")
                .Select(parameter => (string?)parameter.Attribute("Name")),
            document.Descendants(rdlNamespace + "CellDefinition")
                .Select(cell => cell.Element(rdlNamespace + "ParameterName")?.Value));
        Assert.Equal("System.Decimal",
            datasets.Single(dataset => (string?)dataset.Attribute("Name") == "DataSet_source2")
                .Descendants(XNamespace.Get(
                    "http://schemas.microsoft.com/SQLServer/reporting/reportdesigner") + "TypeName")
                .Last().Value);
    }

    [Fact]
    public void ReportPackageValidationRejectsMissingRequiredDataset()
    {
        var package = new PortableReportPackage(
            PortableReportFormat.PackageFormat,
            PortableReportFormat.PackageVersion,
            "SingleSource",
            "Orders",
            null,
            null,
            JsonSerializer.SerializeToElement(
                new PortableSingleReportDefinition(
                    new ReportDesign(
                        "Orders",
                        [new ReportField(0, "OrderId", "Order number", 144)],
                        null,
                        false),
                    []),
                JsonOptions),
            [],
            []);

        var exception = Assert.Throws<PortableReportException>(
            () => PortableReportFormat.Validate(package));

        Assert.Contains("invalid dataset definition", exception.Message);
    }
}
