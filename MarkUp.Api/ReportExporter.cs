using System.Globalization;
using ClosedXML.Excel;
using MarkUp.Shared;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MarkUp.Api;

/// <summary>Exports any ReportTable to Excel (.xlsx) or PDF.</summary>
public sealed class ReportExporter
{
    private readonly AppClock _clock;

    public ReportExporter(AppClock clock) => _clock = clock;

    private static bool TryNumber(string s, out decimal value) =>
        decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    public byte[] ToExcel(ReportTable t)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Report");
        var colCount = Math.Max(1, t.Columns.Count);

        var r = 1;
        ws.Cell(r, 1).Value = t.Title;
        ws.Cell(r, 1).Style.Font.Bold = true;
        ws.Cell(r, 1).Style.Font.FontSize = 14;
        r++;

        foreach (var h in t.Header)
        {
            ws.Cell(r, 1).Value = h.Label;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = h.Value;
            r++;
        }
        ws.Cell(r, 1).Value = "Generated";
        ws.Cell(r, 1).Style.Font.Bold = true;
        ws.Cell(r, 2).Value = AppClock.StampText(_clock.Now);
        r += 2;

        var headerRow = r;
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var cell = ws.Cell(r, c + 1);
            cell.Value = t.Columns[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }
        r++;

        foreach (var row in t.Rows)
        {
            for (var c = 0; c < t.Columns.Count; c++)
            {
                var text = c < row.Count ? row[c] ?? "" : "";
                var cell = ws.Cell(r, c + 1);
                if (t.NumericColumns.Contains(c) && TryNumber(text, out var num))
                {
                    cell.Value = (double)num;
                    cell.Style.NumberFormat.Format = "#,##0.00";
                }
                else
                {
                    // Always stored as text, never as a formula.
                    cell.Value = text;
                }
            }
            r++;
        }

        if (t.Footer.Count > 0)
        {
            r++;
            foreach (var f in t.Footer)
            {
                ws.Cell(r, 1).Value = f.Label;
                ws.Cell(r, 1).Style.Font.Bold = true;
                ws.Cell(r, 2).Value = f.Value;
                r++;
            }
        }

        for (var c = 1; c <= colCount; c++)
        {
            var name = c - 1 < t.Columns.Count ? t.Columns[c - 1] : "";
            ws.Column(c).Width = name is "SN" ? 6 : Math.Clamp(name.Length + 6, 12, 40);
        }
        ws.SheetView.FreezeRows(headerRow);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public byte[] ToPdf(ReportTable t)
    {
        var generated = AppClock.StampText(_clock.Now);
        var columns = t.Columns.Count > 0 ? t.Columns : new List<string> { "" };

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(20);
                page.DefaultTextStyle(x => x.FontSize(8));

                page.Header().Column(col =>
                {
                    col.Item().Text(t.Title).FontSize(14).Bold();
                    foreach (var h in t.Header)
                    {
                        col.Item().Text(text =>
                        {
                            text.Span(h.Label + ": ").SemiBold();
                            text.Span(h.Value);
                        });
                    }
                    col.Item().Text("Generated: " + generated).FontColor(Colors.Grey.Darken1);
                });

                page.Content().PaddingVertical(8).Column(col =>
                {
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(def =>
                        {
                            foreach (var c in columns)
                            {
                                if (c == "SN") def.ConstantColumn(28);
                                else def.RelativeColumn();
                            }
                        });

                        table.Header(header =>
                        {
                            foreach (var c in columns)
                            {
                                header.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text(c).Bold();
                            }
                        });

                        if (t.Rows.Count == 0)
                        {
                            table.Cell().ColumnSpan((uint)columns.Count).Padding(6).Text("No records.");
                        }

                        foreach (var row in t.Rows)
                        {
                            for (var c = 0; c < columns.Count; c++)
                            {
                                var text = c < row.Count ? row[c] ?? "" : "";
                                var cell = table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3);
                                if (t.NumericColumns.Contains(c)) cell.AlignRight().Text(text);
                                else cell.Text(text);
                            }
                        }
                    });

                    if (t.Footer.Count > 0)
                    {
                        col.Item().PaddingTop(10).Column(f =>
                        {
                            foreach (var item in t.Footer)
                            {
                                f.Item().Text(text =>
                                {
                                    text.Span(item.Label + ": ").SemiBold();
                                    text.Span(item.Value);
                                });
                            }
                        });
                    }
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        }).GeneratePdf();
    }
}
