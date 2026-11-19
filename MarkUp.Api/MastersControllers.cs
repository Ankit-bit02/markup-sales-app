using System.Reflection;
using MarkUp.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarkUp.Api;

/// <summary>
/// Create / edit / deactivate for one master type. Admin only.
/// Delete = deactivate, so history and reports stay correct.
/// </summary>
[ApiController]
[Authorize(Roles = Roles.Admin)]
public abstract class MasterControllerBase<T> : ControllerBase where T : class, IEntity, IActivatable, new()
{
    protected readonly SheetStore Store;

    protected MasterControllerBase(SheetStore store) => Store = store;

    /// <summary>Throws AppException when the item is invalid. Runs inside the write lock.</summary>
    protected abstract Task ValidateAsync(T item, List<T> all);

    [HttpGet]
    public async Task<ActionResult<List<T>>> GetAll()
    {
        var all = await Store.GetAllAsync<T>();
        return Ok(all);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<T>> Get(string id)
    {
        var item = await Store.FindAsync<T>(id);
        return item == null ? NotFound(new ApiError { Message = "Record not found." }) : Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<T>> Create([FromBody] T item)
    {
        TrimStrings(item);
        item.Id = Ids.New();
        await Store.WriteLockedAsync(async () =>
        {
            var all = await Store.GetAllAsync<T>();
            await ValidateAsync(item, all);
            await Store.AddAsync(item);
        });
        return Ok(item);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<T>> Update(string id, [FromBody] T item)
    {
        TrimStrings(item);
        item.Id = id;
        await Store.WriteLockedAsync(async () =>
        {
            var all = await Store.GetAllAsync<T>();
            if (all.All(x => x.Id != id)) throw AppException.NotFound("Record not found.");
            await ValidateAsync(item, all);
            await Store.UpdateAsync(item);
        });
        return Ok(item);
    }

    /// <summary>Deactivates the record (soft delete).</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Deactivate(string id)
    {
        await Store.WriteLockedAsync(async () =>
        {
            var item = await Store.FindAsync<T>(id) ?? throw AppException.NotFound("Record not found.");
            item.IsActive = false;
            await Store.UpdateAsync(item);
        });
        return NoContent();
    }

    private static void TrimStrings(T item)
    {
        foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.PropertyType == typeof(string) && p.CanWrite && p.CanRead)
            {
                var v = (string?)p.GetValue(item);
                p.SetValue(item, v?.Trim() ?? "");
            }
        }
    }

    protected static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

// ---------------------------------------------------------------- Master settings

[Route("api/masters/party-types")]
public sealed class PartyTypesController : MasterControllerBase<PartyType>
{
    public PartyTypesController(SheetStore store) : base(store) { }

    protected override Task ValidateAsync(PartyType item, List<PartyType> all)
    {
        if (all.Any(x => x.Id != item.Id && Same(x.Name, item.Name)))
            throw AppException.Conflict($"Party type \"{item.Name}\" already exists.");
        return Task.CompletedTask;
    }
}

[Route("api/masters/holidays")]
public sealed class HolidaysController : MasterControllerBase<Holiday>
{
    public HolidaysController(SheetStore store) : base(store) { }

    protected override Task ValidateAsync(Holiday item, List<Holiday> all)
    {
        if (item.Date == default) throw AppException.BadRequest("Holiday date is required.");
        if (item.IsActive && all.Any(x => x.Id != item.Id && x.IsActive && x.Date == item.Date))
            throw AppException.Conflict($"A holiday on {item.Date:yyyy-MM-dd} already exists.");
        return Task.CompletedTask;
    }
}

[Route("api/masters/calendar-settings")]
public sealed class CalendarSettingsController : MasterControllerBase<CalendarSetting>
{
    public CalendarSettingsController(SheetStore store) : base(store) { }

    protected override Task ValidateAsync(CalendarSetting item, List<CalendarSetting> all)
    {
        var parts = (item.WeeklyOffDays ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var days = new List<DayOfWeek>();
        foreach (var part in parts)
        {
            if (!Enum.TryParse<DayOfWeek>(part, true, out var d) || int.TryParse(part, out _))
                throw AppException.BadRequest($"\"{part}\" is not a day name. Use e.g. Saturday or Saturday,Sunday.");
            if (!days.Contains(d)) days.Add(d);
        }
        if (days.Count >= 7) throw AppException.BadRequest("At least one working day per week is required.");
        item.WeeklyOffDays = string.Join(",", days.OrderBy(d => (int)d));

        if (item.IsActive && all.Any(x => x.Id != item.Id && x.IsActive && x.Year == item.Year))
            throw AppException.Conflict($"Calendar settings for {item.Year} already exist. Edit that record instead.");
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------- Masters

[Route("api/masters/dealers")]
public sealed class DealersController : MasterControllerBase<Dealer>
{
    public DealersController(SheetStore store) : base(store) { }

    protected override Task ValidateAsync(Dealer item, List<Dealer> all)
    {
        if (all.Any(x => x.Id != item.Id && Same(x.Name, item.Name)))
            throw AppException.Conflict($"Dealer \"{item.Name}\" already exists.");
        return Task.CompletedTask;
    }
}

[Route("api/masters/beats")]
public sealed class BeatsController : MasterControllerBase<Beat>
{
    public BeatsController(SheetStore store) : base(store) { }

    protected override async Task ValidateAsync(Beat item, List<Beat> all)
    {
        var dealer = await Store.FindAsync<Dealer>(item.DealerId);
        if (dealer == null || !dealer.IsActive) throw AppException.BadRequest("Select an active dealer.");

        var user = await Store.FindAsync<UserRecord>(item.AssignedUserId);
        if (user == null || !user.IsActive || user.Role != Roles.Sales)
            throw AppException.BadRequest("Assign the beat to an active Sales user.");
        if (user.DealerId != item.DealerId)
            throw AppException.BadRequest($"{user.Name} belongs to a different dealer. A beat's salesperson must belong to the beat's dealer.");

        if (all.Any(x => x.Id != item.Id && x.DealerId == item.DealerId && Same(x.Name, item.Name)))
            throw AppException.Conflict($"Beat \"{item.Name}\" already exists for this dealer.");
    }
}

[Route("api/masters/parties")]
public sealed class PartiesController : MasterControllerBase<Party>
{
    public PartiesController(SheetStore store) : base(store) { }

    protected override async Task ValidateAsync(Party item, List<Party> all)
    {
        var type = await Store.FindAsync<PartyType>(item.PartyTypeId);
        if (type == null || !type.IsActive) throw AppException.BadRequest("Select an active party type.");

        var beat = await Store.FindAsync<Beat>(item.BeatId);
        if (beat == null || !beat.IsActive) throw AppException.BadRequest("Select an active beat.");

        if (all.Any(x => x.Id != item.Id && x.BeatId == item.BeatId && Same(x.Name, item.Name)))
            throw AppException.Conflict($"Party \"{item.Name}\" already exists in this beat.");
    }
}

[Route("api/masters/products")]
public sealed class ProductsController : MasterControllerBase<Product>
{
    public ProductsController(SheetStore store) : base(store) { }

    protected override Task ValidateAsync(Product item, List<Product> all)
    {
        if (string.IsNullOrWhiteSpace(item.SecondUnit)) item.ConvFactor = 1;
        else if (item.ConvFactor < 1) throw AppException.BadRequest("Conversion must be at least 1 when a 2nd unit is set.");

        if (all.Any(x => x.Id != item.Id && Same(x.Code, item.Code)))
            throw AppException.Conflict($"Product code \"{item.Code}\" is already used.");
        return Task.CompletedTask;
    }
}

[Route("api/masters/schemes")]
public sealed class SchemesController : MasterControllerBase<Scheme>
{
    public SchemesController(SheetStore store) : base(store) { }

    protected override async Task ValidateAsync(Scheme item, List<Scheme> all)
    {
        if (!SchemeTypes.IsValid(item.Type)) throw AppException.BadRequest("Select a scheme type.");

        var product = await Store.FindAsync<Product>(item.ProductId);
        if (product == null || !product.IsActive) throw AppException.BadRequest("Select an active product.");

        if (item.StartDate == default || item.EndDate == default)
            throw AppException.BadRequest("Start and end dates are required.");
        if (item.EndDate < item.StartDate) throw AppException.BadRequest("End date must be on or after the start date.");

        if (item.Type == SchemeTypes.Percent && (item.Value <= 0 || item.Value > 100))
            throw AppException.BadRequest("Percent discount must be between 0 and 100.");
        if (item.Type != SchemeTypes.Percent && item.Value <= 0)
            throw AppException.BadRequest("Scheme value must be greater than 0.");

        if (all.Any(x => x.Id != item.Id && Same(x.Name, item.Name) && x.ProductId == item.ProductId))
            throw AppException.Conflict($"Scheme \"{item.Name}\" already exists for this product.");
    }
}

[Route("api/masters/targets")]
public sealed class TargetsController : MasterControllerBase<Target>
{
    public TargetsController(SheetStore store) : base(store) { }

    protected override async Task ValidateAsync(Target item, List<Target> all)
    {
        var user = await Store.FindAsync<UserRecord>(item.UserId);
        if (user == null || user.Role != Roles.Sales) throw AppException.BadRequest("Select a Sales user.");

        if (item.IsActive && all.Any(x => x.Id != item.Id && x.IsActive && x.UserId == item.UserId && x.Year == item.Year && x.Month == item.Month))
            throw AppException.Conflict($"{user.Name} already has a value target for {item.Month}/{item.Year}. Edit that record instead.");
    }
}

[Route("api/masters/product-targets")]
public sealed class ProductTargetsController : MasterControllerBase<ProductTarget>
{
    public ProductTargetsController(SheetStore store) : base(store) { }

    protected override async Task ValidateAsync(ProductTarget item, List<ProductTarget> all)
    {
        var user = await Store.FindAsync<UserRecord>(item.UserId);
        if (user == null || user.Role != Roles.Sales) throw AppException.BadRequest("Select a Sales user.");

        var product = await Store.FindAsync<Product>(item.ProductId);
        if (product == null) throw AppException.BadRequest("Select a product.");

        if (item.IsActive && all.Any(x => x.Id != item.Id && x.IsActive && x.UserId == item.UserId
                && x.Year == item.Year && x.Month == item.Month && x.ProductId == item.ProductId))
            throw AppException.Conflict($"{user.Name} already has a target for {product.Name} in {item.Month}/{item.Year}. Edit that record instead.");
    }
}
