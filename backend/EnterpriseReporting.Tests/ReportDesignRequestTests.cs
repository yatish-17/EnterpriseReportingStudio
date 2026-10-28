using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using EnterpriseReporting.Api.Controllers;
using EnterpriseReporting.Application.Reports;

namespace EnterpriseReporting.Tests;

public sealed class ReportDesignRequestTests
{
    [Fact]
    public void ToDesign_PreservesLegacyChartDefinitions()
    {
        var chart = new ReportChartRequest("Bar", 0, 1);
        var request = new ReportDesignRequest(
            "Regional Sales",
            [new ReportFieldRequest(0, "Region", "Region", 160)],
            chart);

        var design = request.ToDesign();

        Assert.Null(design.IncludeTable);
        Assert.Equal(chart.ToChart(), design.Chart);
        Assert.Equal(chart.ToChart(), Assert.Single(design.Charts!));
    }

    [Fact]
    public void ToDesign_MapsMultipleChartsAndOptionalTable()
    {
        var charts = new[]
        {
            new ReportChartRequest("Bar", 0, 1),
            new ReportChartRequest("Line", 0, 2)
        };
        var request = new ReportDesignRequest(
            "Regional Sales",
            [new ReportFieldRequest(0, "Region", "Region", 160)],
            IncludeTable: false,
            Charts: charts,
            ComponentOrder: [1, -1, 0]);

        var design = request.ToDesign();

        Assert.False(design.IncludeTable);
        Assert.Equal(charts.Select(chart => chart.ToChart()), design.Charts!);
        Assert.Equal(charts[0].ToChart(), design.Chart);
        Assert.Equal(new[] { 1, -1, 0 }, design.ComponentOrder);
    }

    [Fact]
    public void MultipleChartsSurviveReportDefinitionSerialization()
    {
        var request = new ReportDesignRequest(
            "Regional Sales",
            [new ReportFieldRequest(0, "Region", "Region", 160)],
            IncludeTable: true,
            Charts:
            [
                new ReportChartRequest("Bar", 0, 1),
                new ReportChartRequest("Line", 0, 2),
                new ReportChartRequest("Pie", 0, 3)
            ],
            ComponentOrder: [-1, 0, 1, 2]);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var saved = JsonSerializer.Serialize(request.ToDesign(), options);
        var loaded = JsonSerializer.Deserialize<ReportDesign>(saved, options);

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded.Charts?.Count);
        Assert.Equal(new[] { -1, 0, 1, 2 }, loaded.ComponentOrder);
        Assert.Equal(
            new[] { "Bar", "Line", "Pie" },
            loaded.Charts?.Select(chart => chart.Type));
    }

    [Fact]
    public void MultiSourceTableSortingSurvivesReportDefinitionSerialization()
    {
        var field = new MultiSourceField("source1", "Net_Sales_INR", "Net sales");
        var component = new MultiSourceComponent(
            "sales-table", "Table", "Sales", [field],
            SortField: field,
            SortDirection: "Descending");
        var design = new MultiSourceDesign(
            "MultiSource", "Regional sales",
            [new MultiSourceDataset("source1", 1), new MultiSourceDataset("source2", 2)],
            [],
            [component],
            []);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var saved = JsonSerializer.Serialize(design, options);
        var loaded = JsonSerializer.Deserialize<MultiSourceDesign>(saved, options);

        Assert.NotNull(loaded);
        var loadedComponent = Assert.Single(loaded.Components);
        Assert.Equal(field, loadedComponent.SortField);
        Assert.Equal("Descending", loadedComponent.SortDirection);
    }

    [Fact]
    public void PrintHeaderSurvivesReportDefinitionSerialization()
    {
        var header = new ReportPrintHeader(
            "BMRCL Report",
            "Operations",
            null,
            "center",
            [
                new ReportPrintHeaderElement("title", 82, 18),
                new ReportPrintHeaderElement("subtitle", 20, 78)
            ]);
        var request = new ReportDesignRequest(
            "Operations report",
            [new ReportFieldRequest(0, "Station", "Station", 160)],
            PrintHeader: new ReportPrintHeaderRequest(
                header.Title,
                header.Subtitle,
                header.LogoDataUrl,
                header.Alignment,
                header.Elements?.Select(element =>
                    new ReportPrintHeaderElementRequest(element.Type, element.X, element.Y, element.Size)).ToArray()));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var saved = JsonSerializer.Serialize(request.ToDesign(), options);
        var loaded = JsonSerializer.Deserialize<ReportDesign>(saved, options);

        Assert.NotNull(loaded?.PrintHeader);
        Assert.Equal(header.Title, loaded.PrintHeader.Title);
        Assert.Equal(header.Subtitle, loaded.PrintHeader.Subtitle);
        Assert.Equal(header.Alignment, loaded.PrintHeader.Alignment);
        Assert.Equal(header.Elements, loaded.PrintHeader.Elements);
        Assert.True(loaded.PrintHeader.IsValid());
    }

    [Fact]
    public void PrintHeaderRejectsUnsupportedOrMalformedLogos()
    {
        Assert.False(new ReportPrintHeader(
            "Report", null, "data:image/svg+xml;base64,PHN2Zz4=", "left").IsValid());
        Assert.False(new ReportPrintHeader(
            "Report", null, "data:image/png;base64,ZmFrZQ==", "left").IsValid());
        Assert.False(new ReportPrintHeader(
            "Report", null, null, "diagonal").IsValid());
        Assert.False(new ReportPrintHeader(
            "Report", null, null, "left",
            [new ReportPrintHeaderElement("title", 101, 20)]).IsValid());
        Assert.False(new ReportPrintHeader(
            "Report", null, null, "left",
            [new ReportPrintHeaderElement("title", 10, 20, 37)]).IsValid());
        Assert.False(new ReportPrintHeader(
            "Report", null, null, "left", [], "legacy-peach-v1").IsValid());
        Assert.False(new ReportPrintHeader(
            "Report", null, "data:image/jpeg;base64,/9j/2Q==", "left", [], "unsupported").IsValid());
    }

    [Fact]
    public void FixedOrganizationHeaderIsValidatedAndSerialized()
    {
        var headerRequest = new ReportPrintHeaderRequest(
            "TVS",
            null,
            "data:image/jpeg;base64,/9j/2Q==",
            "left",
            [],
            "organization-header-v1");
        var design = new ReportDesignRequest(
            "Operations report",
            [new ReportFieldRequest(0, "Station", "Station", 160)],
            PrintHeader: headerRequest).ToDesign();

        Assert.Equal("organization-header-v1", design.PrintHeader?.Template);
        Assert.True(design.PrintHeader!.IsValid());
        var serialized = JsonSerializer.Serialize(design);
        var loaded = JsonSerializer.Deserialize<ReportDesign>(serialized);
        Assert.Equal("organization-header-v1", loaded?.PrintHeader?.Template);
    }

    [Fact]
    public void OlderVersionedOrganizationHeaderMarkersRemainSupported()
    {
        var header = new ReportPrintHeader(
            "Report",
            null,
            "data:image/jpeg;base64,/9j/2Q==",
            "left",
            [],
            "legacy-report-v2");

        Assert.True(header.IsValid());
    }

    [Fact]
    public void OrganizationHeaderAllowsTextOnlyOrLogoOnly()
    {
        var textOnly = new ReportPrintHeader(
            "Quarterly report",
            null,
            null,
            "left",
            [new ReportPrintHeaderElement("title", 10, 24)]);
        var logoOnly = new ReportPrintHeader(
            "",
            null,
            "data:image/jpeg;base64,/9j/2Q==",
            "left",
            [new ReportPrintHeaderElement("logo", 7, 40)]);

        Assert.True(textOnly.IsValid());
        Assert.True(logoOnly.IsValid());
    }

    [Fact]
    public void ExcelWorkbookEmbedsFixedHeaderArtworkAcrossTopRows()
    {
        var summary = new ReportSummary(
            1, "Stations", null, null, 1, 1, "Published", true,
            DateTimeOffset.UtcNow, false, false);
        IReadOnlyList<IReadOnlyList<JsonElement>> rows =
            [new[] { JsonSerializer.SerializeToElement("Central") }];
        var execution = new ReportExecution(
            summary,
            [new ReportField(0, "Station", "Station", 160)],
            rows,
            false,
            1);
        var header = new ReportPrintHeader(
            "TVS",
            null,
            "data:image/jpeg;base64,/9j/2Q==",
            "left",
            [],
            "organization-header-v1");
        var createWorkbook = typeof(ReportsController).GetMethod(
            "CreateWorkbook", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(createWorkbook);
        var workbook = Assert.IsType<byte[]>(createWorkbook.Invoke(null, [execution, header]));

        using var archive = new ZipArchive(new MemoryStream(workbook), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("xl/media/company-logo.jpeg"));
        using var worksheetStream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var worksheetDocument = XDocument.Load(worksheetStream);
        Assert.Contains(worksheetDocument.Descendants(),
            element => element.Name.LocalName == "row" &&
                (string?)element.Attribute("r") == "3");
        Assert.Contains(worksheetDocument.Descendants(),
            element => element.Name.LocalName == "row" &&
                (string?)element.Attribute("r") == "4");
        using var drawingStream = archive.GetEntry("xl/drawings/drawing1.xml")!.Open();
        var drawingDocument = XDocument.Load(drawingStream);
        Assert.Contains(drawingDocument.Descendants(),
            element => element.Name.LocalName == "col" && element.Value == "0");
        Assert.Contains(drawingDocument.Descendants(),
            element => element.Name.LocalName == "row" && element.Value == "0");
    }

    [Fact]
    public void ExcelWorkbookPlacesTextHeaderItemsAndRepeatsHeaderRowsForPrinting()
    {
        var summary = new ReportSummary(
            1, "Stations", null, null, 1, 1, "Published", true,
            DateTimeOffset.UtcNow, false, false);
        IReadOnlyList<IReadOnlyList<JsonElement>> rows =
            [new[] { JsonSerializer.SerializeToElement("Central") }];
        var execution = new ReportExecution(
            summary,
            [new ReportField(0, "Station", "Station", 160)],
            rows,
            false,
            1);
        var header = new ReportPrintHeader(
            "BMRCL Report",
            "Operations",
            null,
            "left",
            [
                new ReportPrintHeaderElement("title", 82, 18, 24),
                new ReportPrintHeaderElement("subtitle", 20, 78, 16)
            ]);
        var createWorkbook = typeof(ReportsController).GetMethod(
            "CreateWorkbook", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(createWorkbook);
        var workbook = Assert.IsType<byte[]>(createWorkbook.Invoke(null, [execution, header]));

        using var archive = new ZipArchive(new MemoryStream(workbook), ZipArchiveMode.Read);
        using var workbookStream = archive.GetEntry("xl/workbook.xml")!.Open();
        var workbookDocument = XDocument.Load(workbookStream);
        Assert.Contains(workbookDocument.Descendants(),
            element => (string?)element.Attribute("name") == "_xlnm.Print_Titles");
        using var worksheetStream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var worksheetDocument = XDocument.Load(worksheetStream);
        Assert.DoesNotContain(worksheetDocument.Descendants(),
            element => element.Name.LocalName == "drawing");
        Assert.Contains(worksheetDocument.Descendants(),
            element => element.Name.LocalName == "c" &&
                (string?)element.Attribute("r") == "G1" &&
                element.Descendants().Any(value => value.Value == "BMRCL Report"));
        Assert.Contains(worksheetDocument.Descendants(),
            element => element.Name.LocalName == "c" &&
                (string?)element.Attribute("r") == "B3" &&
                element.Descendants().Any(value => value.Value == "Operations"));
        Assert.Contains(worksheetDocument.Descendants(),
            element => element.Name.LocalName == "c" &&
                (string?)element.Attribute("r") == "G1" &&
                (string?)element.Attribute("s") == "1");
        using var stylesStream = archive.GetEntry("xl/styles.xml")!.Open();
        var stylesDocument = XDocument.Load(stylesStream);
        Assert.Contains(stylesDocument.Descendants(),
            element => element.Name.LocalName == "sz" && (string?)element.Attribute("val") == "24");
        Assert.Contains(stylesDocument.Descendants(),
            element => element.Name.LocalName == "sz" && (string?)element.Attribute("val") == "16");
    }
}
