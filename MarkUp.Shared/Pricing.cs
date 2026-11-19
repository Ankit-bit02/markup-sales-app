namespace MarkUp.Shared;

/// <summary>Result of pricing one order line.</summary>
public record LineCalc(
    decimal BaseQty,
    decimal Rate,
    decimal Gross,
    decimal Discount,
    decimal FreeQty,
    decimal LineValue,
    bool SchemeApplied);

/// <summary>
/// Pricing rules shared by the app (live preview) and the API (authoritative calculation).
/// The API always recalculates; totals sent by a device are never trusted.
/// </summary>
public static class Pricing
{
    /// <summary>Converts an entered quantity to base units.</summary>
    public static decimal ToBaseQty(Product product, decimal qty, string unit)
    {
        var factor = product.ConvFactor <= 0 ? 1 : product.ConvFactor;
        return unit == UnitKinds.Second ? qty * factor : qty;
    }

    /// <summary>True when the scheme is valid for this product, date and quantity.</summary>
    public static bool SchemeApplies(Scheme? scheme, Product product, decimal baseQty, DateOnly date)
    {
        if (scheme is null) return false;
        return scheme.IsActive
            && scheme.ProductId == product.Id
            && date >= scheme.StartDate
            && date <= scheme.EndDate
            && baseQty >= scheme.MinQty;
    }

    /// <summary>
    /// Prices a line at the retailer rate (per base unit).
    /// Percent: discount = gross x value / 100.
    /// Flat: discount = value (NPR) on the line, capped at gross.
    /// FreeQty: value base units given free; line value unchanged.
    /// </summary>
    public static LineCalc Calculate(Product product, decimal qty, string unit, Scheme? scheme, DateOnly date)
    {
        var baseQty = Math.Round(ToBaseQty(product, qty, unit), 3);
        var rate = product.RetailerRate;
        var gross = Math.Round(baseQty * rate, 2);
        decimal discount = 0;
        decimal free = 0;
        var applied = SchemeApplies(scheme, product, baseQty, date);

        if (applied && scheme is not null)
        {
            switch (scheme.Type)
            {
                case SchemeTypes.Percent:
                    var pct = Math.Clamp(scheme.Value, 0, 100);
                    discount = Math.Round(gross * pct / 100m, 2);
                    break;
                case SchemeTypes.Flat:
                    discount = Math.Min(Math.Round(scheme.Value, 2), gross);
                    break;
                case SchemeTypes.FreeQty:
                    free = scheme.Value;
                    break;
                default:
                    applied = false;
                    break;
            }
        }

        var lineValue = Math.Max(0, gross - discount);
        return new LineCalc(baseQty, rate, gross, discount, free, lineValue, applied);
    }

    /// <summary>Weekly off days parsed from text like "Saturday,Sunday".</summary>
    public static HashSet<DayOfWeek> ParseWeeklyOffs(string? text)
    {
        var set = new HashSet<DayOfWeek>();
        if (string.IsNullOrWhiteSpace(text)) return set;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<DayOfWeek>(part, true, out var d)) set.Add(d);
        }
        return set;
    }

    /// <summary>Counts working days from start to end inclusive.</summary>
    public static int WorkingDays(DateOnly start, DateOnly end, ISet<DayOfWeek> weeklyOffs, ISet<DateOnly> holidays)
    {
        var count = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (weeklyOffs.Contains(d.DayOfWeek)) continue;
            if (holidays.Contains(d)) continue;
            count++;
        }
        return count;
    }
}
