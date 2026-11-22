using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using MarkUp.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarkUp.Api;

/// <summary>
/// Bulk import of products from an Excel (.xlsx) or CSV file.
/// The same upload runs twice: first as a preview (nothing saved), then with apply=true.
/// All rows go into the brand chosen on upload.
/// Products are matched by Code when the file has one, otherwise by Name inside that brand
/// (products that have no brand yet are matched too, and get the brand).
/// Existing products are updated; products missing from the file are never deactivated.
/// </summary>
[ApiController]
[Route("api/masters/products/import")]
[Authorize(Roles = Roles.Admin)]
public sealed class ProductImportController : ControllerBase
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxRows = 3000;

    private readonly SheetStore _store;

    public ProductImportController(SheetStore store) => _store = store;

    [HttpPost]
    [RequestSizeLimit(MaxFileBytes + 64 * 1024)]
    public async Task<ActionResult<ProductImportResult>> Import(IFormFile? file, [FromQuery] string? brandId, [FromQuery] bool apply = false)
    {
        if (string.IsNullOrWhiteSpace(brandId)) throw AppException.BadRequest("Select the brand for these products.");
        if (file == null || file.Length == 0) throw AppException.BadRequest("Choose a file to upload.");
        if (file.Length > MaxFileBytes) throw AppException.BadRequest("The file is larger than 5 MB.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        List<List<string>> grid;
        await using (var stream = file.OpenReadStream())
        {
            grid = ext switch
            {
                ".xlsx" or ".xlsm" => ReadExcel(stream),
                ".csv" => await ReadCsvAsync(stream),
                ".xls" => throw AppException.BadRequest("Old .xls files are not supported. Open the file in Excel and save it as .xlsx, then upload again."),
                _ => throw AppException.BadRequest("Upload an Excel (.xlsx) or CSV file.")
            };
        }

        var parsed = ParseGrid(grid);
        var result = new ProductImportResult { FileName = Path.GetFileName(file.FileName), MappedColumns = parsed.Mapped };

        await _store.WriteLockedAsync(async () =>
        {
            var brands = await _store.GetAllAsync<Brand>();
            var brand = brands.FirstOrDefault(b => b.Id == brandId) ?? throw AppException.BadRequest("The selected brand was not found.");
            if (!brand.IsActive) throw AppException.BadRequest($"Brand \"{brand.Name}\" is inactive. Activate it first.");
            result.BrandName = brand.Name;

            var existing = await _store.GetAllAsync<Product>();
            var plan = BuildPlan(parsed, existing, brand, brands);
            result.Rows = plan.Rows;

            if (apply)
            {
                await _store.AddRangeAsync(plan.ToAdd);
                await _store.UpdateRangeAsync(plan.ToUpdate);
                result.Applied = true;
            }
        });

        result.NewCount = result.Rows.Count(r => r.Action == ImportActions.New);
        result.UpdateCount = result.Rows.Count(r => r.Action == ImportActions.Update);
        result.UnchangedCount = result.Rows.Count(r => r.Action == ImportActions.Unchanged);
        result.ErrorCount = result.Rows.Count(r => r.Action == ImportActions.Error);
        return Ok(result);
    }

    /// <summary>An empty Excel file with the expected columns and one example row.</summary>
    [HttpGet("template")]
    public IActionResult Template()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Products");
        string[] headers = { "Code", "Name", "Group Name", "Main Unit", "Alt. Unit", "CON Factor", "MRP", "MRP INR", "DLR Rate", "Sale Price" };
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF9");

        ws.Cell(2, 1).Value = "P-1001";
        ws.Cell(2, 2).Value = "Example Product 100gm Box";
        ws.Cell(2, 3).Value = "Example Group";
        ws.Cell(2, 4).Value = "Pc";
        ws.Cell(2, 5).Value = "Cs";
        ws.Cell(2, 6).Value = 120;
        ws.Cell(2, 7).Value = 145;
        ws.Cell(2, 8).Value = 0;
        ws.Cell(2, 9).Value = 0;
        ws.Cell(2, 10).Value = 93.92;

        var notes = wb.Worksheets.Add("How to fill");
        string[] lines =
        {
            "Choose the brand in the app when uploading. All rows in one file go into that brand.",
            "Required: Name and Main Unit.",
            "Code: optional. Leave empty and the app creates one (P-0001...). Rows are matched to existing products by Code, or by Name when there is no code.",
            "Alt. Unit + CON Factor: e.g. Cs and 120 means 1 Cs = 120 Pc. Leave both empty if there is no 2nd unit.",
            "Sale Price = Retailer (RT) rate per Main Unit. Orders are priced with this rate.",
            "Existing products are updated. Products not in the file are NOT deactivated.",
            "Delete the example row before uploading."
        };
        for (var i = 0; i < lines.Length; i++) notes.Cell(i + 1, 1).Value = lines[i];

        ws.Columns().AdjustToContents();
        notes.Column(1).Width = 110;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Product-Import-Template.xlsx");
    }

    // ------------------------------------------------------------------ Reading files

    private static List<List<string>> ReadExcel(Stream stream)
    {
        XLWorkbook wb;
        try
        {
            wb = new XLWorkbook(stream);
        }
        catch (Exception)
        {
            throw AppException.BadRequest("The Excel file could not be read. Open it in Excel, save it again as .xlsx and retry.");
        }

        using (wb)
        {
            var ws = wb.Worksheets.FirstOrDefault(w => w.LastRowUsed() != null)
                     ?? throw AppException.BadRequest("The Excel file is empty.");
            var lastRow = ws.LastRowUsed()!.RowNumber();
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            if (lastRow > MaxRows + 20) throw AppException.BadRequest($"The file has too many rows (max {MaxRows}).");
            if (lastCol > 60) lastCol = 60;

            var grid = new List<List<string>>(lastRow);
            for (var r = 1; r <= lastRow; r++)
            {
                var row = new List<string>(lastCol);
                for (var c = 1; c <= lastCol; c++) row.Add(CellText(ws.Cell(r, c)));
                grid.Add(row);
            }
            return grid;
        }
    }

    private static string CellText(IXLCell cell)
    {
        var v = cell.HasFormula ? cell.CachedValue : cell.Value;
        if (v.IsBlank) return "";
        if (v.IsNumber) return v.GetNumber().ToString("0.############", CultureInfo.InvariantCulture);
        if (v.IsText) return v.GetText().Trim();
        if (v.IsBoolean) return v.GetBoolean() ? "TRUE" : "FALSE";
        if (v.IsDateTime) return v.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return v.ToString().Trim();
    }

    private static async Task<List<List<string>>> ReadCsvAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync();
        var grid = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') inQuotes = false;
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString().Trim()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString().Trim());
                    field.Clear();
                    grid.Add(row);
                    row = new List<string>();
                    if (grid.Count > MaxRows + 20) throw AppException.BadRequest($"The file has too many rows (max {MaxRows}).");
                    break;
                default: field.Append(ch); break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString().Trim());
            grid.Add(row);
        }
        return grid;
    }

    // ------------------------------------------------------------------ Columns

    private enum Col { Code, Name, Group, Unit, SecondUnit, Conv, Mrp, MrpInr, Dealer, Retail }

    /// <summary>Header names we recognise (compared without spaces, dots or case).</summary>
    private static readonly Dictionary<Col, string[]> Aliases = new()
    {
        [Col.Code] = new[] { "code", "productcode", "itemcode", "alias", "sku" },
        [Col.Name] = new[] { "name", "productname", "itemname", "particulars", "product", "item" },
        [Col.Group] = new[] { "groupname", "group", "category", "productgroup", "itemgroup" },
        [Col.Unit] = new[] { "mainunit", "unit", "baseunit", "uom" },
        [Col.SecondUnit] = new[] { "altunit", "alternateunit", "secondunit", "2ndunit" },
        [Col.Conv] = new[] { "confactor", "conversionfactor", "conversion", "convfactor" },
        [Col.Mrp] = new[] { "mrp", "mrpnpr" },
        [Col.MrpInr] = new[] { "mrpinr" },
        [Col.Dealer] = new[] { "dlrrate", "dealerrate", "dlr" },
        [Col.Retail] = new[] { "saleprice", "rtrate", "retailerrate", "sellingprice", "rate", "rt" }
    };

    private static readonly Dictionary<Col, string> FieldLabels = new()
    {
        [Col.Code] = "Code",
        [Col.Name] = "Product name",
        [Col.Group] = "Group name",
        [Col.Unit] = "Base unit",
        [Col.SecondUnit] = "2nd unit",
        [Col.Conv] = "Base units in one 2nd unit",
        [Col.Mrp] = "MRP (NPR)",
        [Col.MrpInr] = "MRP (INR)",
        [Col.Dealer] = "Dealer rate (DLR)",
        [Col.Retail] = "Retailer rate (RT)"
    };

    private static readonly Regex NonAlnum = new("[^a-z0-9]", RegexOptions.Compiled);

    private static string Norm(string s) => NonAlnum.Replace(s.ToLowerInvariant(), "");

    private sealed class ParsedFile
    {
        public Dictionary<Col, int> Columns { get; } = new();
        public List<string> Mapped { get; } = new();
        public List<(int RowNumber, List<string> Cells)> Rows { get; } = new();
    }

    private static ParsedFile ParseGrid(List<List<string>> grid)
    {
        // Find the header row: the first of the top 15 rows that has a Name column and one more known column.
        for (var r = 0; r < Math.Min(grid.Count, 15); r++)
        {
            var map = MapHeader(grid[r]);
            if (!map.ContainsKey(Col.Name) || map.Count < 2) continue;

            var parsed = new ParsedFile();
            foreach (var (col, idx) in map)
            {
                parsed.Columns[col] = idx;
                parsed.Mapped.Add($"{grid[r][idx]} → {FieldLabels[col]}");
            }

            for (var i = r + 1; i < grid.Count; i++)
            {
                var cells = grid[i];
                if (cells.All(string.IsNullOrWhiteSpace)) continue;
                parsed.Rows.Add((i + 1, cells));
            }
            if (parsed.Rows.Count == 0) throw AppException.BadRequest("The file has a header row but no products under it.");
            if (parsed.Rows.Count > MaxRows) throw AppException.BadRequest($"The file has too many rows (max {MaxRows}).");
            return parsed;
        }
        throw AppException.BadRequest("Could not find the header row. The file needs a \"Name\" column and at least one more column such as \"Main Unit\" or \"MRP\". Download the template to see the expected columns.");
    }

    private static Dictionary<Col, int> MapHeader(List<string> cells)
    {
        var map = new Dictionary<Col, int>();
        for (var c = 0; c < cells.Count; c++)
        {
            var h = Norm(cells[c]);
            if (h.Length == 0) continue;
            foreach (var (col, names) in Aliases)
            {
                if (map.ContainsKey(col) || !names.Contains(h)) continue;
                map[col] = c;
                break;
            }
        }
        return map;
    }

    // ------------------------------------------------------------------ Plan

    private sealed class Plan
    {
        public List<ProductImportRow> Rows { get; } = new();
        public List<Product> ToAdd { get; } = new();
        public List<Product> ToUpdate { get; } = new();
    }

    private static readonly Regex AutoCode = new(@"^P-(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static Plan BuildPlan(ParsedFile file, List<Product> existing, Brand brand, List<Brand> brands)
    {
        var plan = new Plan();
        var brandIds = brands.Select(b => b.Id).ToHashSet();
        bool Unassigned(Product p) => string.IsNullOrEmpty(p.BrandId) || !brandIds.Contains(p.BrandId);
        string BrandName(string id) => brands.FirstOrDefault(b => b.Id == id)?.Name ?? "another brand";

        var byCode = existing.GroupBy(p => p.Code.Trim(), StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Name matching stays inside the chosen brand; products without a brand are adopted.
        Dictionary<string, Product> NameIndex(IEnumerable<Product> items) =>
            items.GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                 .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byNameInBrand = NameIndex(existing.Where(p => p.BrandId == brand.Id));
        var byNameUnassigned = NameIndex(existing.Where(Unassigned));

        var nextNumber = existing.Select(p => AutoCode.Match(p.Code.Trim()))
                                 .Where(m => m.Success)
                                 .Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0)
                                 .DefaultIfEmpty(0).Max() + 1;

        var seenNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenCodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var touched = new HashSet<string>();
        var hasMrpInr = file.Columns.ContainsKey(Col.MrpInr);
        var hasDealer = file.Columns.ContainsKey(Col.Dealer);
        var hasRetail = file.Columns.ContainsKey(Col.Retail);
        var hasMrp = file.Columns.ContainsKey(Col.Mrp);
        var hasGroup = file.Columns.ContainsKey(Col.Group);

        foreach (var (rowNumber, cells) in file.Rows)
        {
            string Get(Col c) => file.Columns.TryGetValue(c, out var i) && i < cells.Count ? cells[i].Trim() : "";

            var row = new ProductImportRow
            {
                RowNumber = rowNumber,
                Code = Get(Col.Code),
                Name = Regex.Replace(Get(Col.Name), @"\s+", " "),
                GroupName = Get(Col.Group),
                Unit = Get(Col.Unit),
                SecondUnit = Get(Col.SecondUnit)
            };
            plan.Rows.Add(row);

            var errors = new List<string>();
            var conv = ReadNumber(Get(Col.Conv), "CON Factor", errors) ?? 1;
            var mrp = ReadNumber(Get(Col.Mrp), "MRP", errors);
            var mrpInr = ReadNumber(Get(Col.MrpInr), "MRP INR", errors);
            var dealer = ReadNumber(Get(Col.Dealer), "DLR rate", errors);
            var retail = ReadNumber(Get(Col.Retail), "Sale price", errors);

            if (row.Name.Length < 2) errors.Add("Name is missing.");
            else if (row.Name.Length > 150) errors.Add("Name is longer than 150 characters.");
            if (row.Unit.Length == 0) errors.Add("Main unit is missing.");
            if (row.Unit.Length > 20 || row.SecondUnit.Length > 20) errors.Add("Unit names can be at most 20 characters.");
            if (row.GroupName.Length > 80) errors.Add("Group name is longer than 80 characters.");
            if (row.Code.Length > 30) errors.Add("Code is longer than 30 characters.");
            if (row.SecondUnit.Length == 0) conv = 1;
            else if (conv < 1 || conv > 100000) errors.Add("CON Factor must be between 1 and 100,000 when an Alt. unit is set.");

            if (row.Name.Length >= 2 && seenNames.TryGetValue(row.Name, out var firstNameRow))
                errors.Add($"Same name as row {firstNameRow}.");
            if (row.Code.Length > 0 && seenCodes.TryGetValue(row.Code, out var firstCodeRow))
                errors.Add($"Same code as row {firstCodeRow}.");

            row.ConvFactor = conv;
            row.MrpNpr = mrp ?? 0;
            row.RetailerRate = retail ?? 0;

            // Match: by code when the file has one, otherwise by name.
            Product? match = null;
            if (row.Code.Length > 0)
            {
                if (byCode.TryGetValue(row.Code, out var byCodeMatch))
                {
                    if (byCodeMatch.BrandId == brand.Id || Unassigned(byCodeMatch)) match = byCodeMatch;
                    else errors.Add($"Code {row.Code} is already used by \"{byCodeMatch.Name}\" in {BrandName(byCodeMatch.BrandId)}.");
                }
            }
            else if (row.Name.Length > 0)
            {
                if (!byNameInBrand.TryGetValue(row.Name, out match)) byNameUnassigned.TryGetValue(row.Name, out match);
            }

            if (match != null && touched.Contains(match.Id))
                errors.Add("Another row in this file already updates this product.");

            // A new code must not clash with a product of the same name in this brand.
            if (match == null && row.Code.Length > 0 && errors.Count == 0
                && byNameInBrand.TryGetValue(row.Name, out var sameName) && !string.Equals(sameName.Code, row.Code, StringComparison.OrdinalIgnoreCase))
                errors.Add($"\"{row.Name}\" already exists in {brand.Name} with code {sameName.Code}.");

            if (row.Name.Length >= 2) seenNames.TryAdd(row.Name, rowNumber);
            if (row.Code.Length > 0) seenCodes.TryAdd(row.Code, rowNumber);

            if (errors.Count > 0)
            {
                row.Action = ImportActions.Error;
                row.Message = string.Join(" ", errors);
                continue;
            }

            if (match == null)
            {
                if (row.Code.Length == 0) row.Code = $"P-{nextNumber++:0000}";
                var product = new Product
                {
                    Id = Ids.New(),
                    BrandId = brand.Id,
                    Code = row.Code,
                    Name = row.Name,
                    GroupName = row.GroupName,
                    Unit = row.Unit,
                    SecondUnit = row.SecondUnit,
                    ConvFactor = conv,
                    MrpNpr = mrp ?? 0,
                    MrpInr = mrpInr ?? 0,
                    DealerRate = dealer ?? 0,
                    RetailerRate = retail ?? 0,
                    IsActive = true
                };
                plan.ToAdd.Add(product);
                row.Action = ImportActions.New;
                row.Message = "New product.";
                continue;
            }

            touched.Add(match.Id);
            row.Code = match.Code;

            // Only columns present in the file change the product; others keep their current value.
            var updated = new Product
            {
                Id = match.Id,
                BrandId = brand.Id,
                Code = match.Code,
                Name = row.Name,
                GroupName = hasGroup ? row.GroupName : match.GroupName,
                Unit = row.Unit,
                SecondUnit = row.SecondUnit,
                ConvFactor = conv,
                MrpNpr = hasMrp ? mrp ?? 0 : match.MrpNpr,
                MrpInr = hasMrpInr ? mrpInr ?? 0 : match.MrpInr,
                DealerRate = hasDealer ? dealer ?? 0 : match.DealerRate,
                RetailerRate = hasRetail ? retail ?? 0 : match.RetailerRate,
                IsActive = match.IsActive
            };

            var changes = Changes(match, updated);
            if (changes.Count == 0)
            {
                row.Action = ImportActions.Unchanged;
                row.Message = "Already up to date.";
            }
            else
            {
                plan.ToUpdate.Add(updated);
                row.Action = ImportActions.Update;
                row.Message = "Changes: " + string.Join(", ", changes) + ".";
            }
            if (!match.IsActive) row.Message += " Product is inactive in the app (stays inactive).";
        }
        return plan;
    }

    private static List<string> Changes(Product a, Product b)
    {
        var list = new List<string>();
        if (a.BrandId != b.BrandId) list.Add("brand set");
        if (a.Name != b.Name) list.Add("name");
        if (a.GroupName != b.GroupName) list.Add("group");
        if (a.Unit != b.Unit || a.SecondUnit != b.SecondUnit || a.ConvFactor != b.ConvFactor) list.Add("units");
        if (a.MrpNpr != b.MrpNpr) list.Add($"MRP {Money(a.MrpNpr)} → {Money(b.MrpNpr)}");
        if (a.MrpInr != b.MrpInr) list.Add("MRP INR");
        if (a.DealerRate != b.DealerRate) list.Add("DLR rate");
        if (a.RetailerRate != b.RetailerRate) list.Add($"RT rate {Money(a.RetailerRate)} → {Money(b.RetailerRate)}");
        return list;
    }

    private static string Money(decimal v) => v.ToString("#,##0.##", CultureInfo.InvariantCulture);

    /// <summary>Empty = null. Accepts "1,234.50". Adds an error for text or negative values.</summary>
    private static decimal? ReadNumber(string text, string label, List<string> errors)
    {
        var t = text.Replace(",", "").Trim();
        if (t.Length == 0) return null;
        if (!decimal.TryParse(t, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var v))
        {
            errors.Add($"{label} \"{text}\" is not a number.");
            return null;
        }
        if (v < 0 || v > 10_000_000)
        {
            errors.Add($"{label} must be between 0 and 10,000,000.");
            return null;
        }
        return Math.Round(v, 4);
    }
}