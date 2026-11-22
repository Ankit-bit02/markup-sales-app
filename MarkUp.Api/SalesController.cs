using DocumentFormat.OpenXml.Spreadsheet;
using MarkUp.Api;
using MarkUp.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/sales")]
[Authorize(Roles = Roles.Sales)]
public sealed class SalesController : ControllerBase
{
    private readonly SalesService _sales;

    public SalesController(SalesService sales) => _sales = sales;

    [HttpGet("bootstrap")]
    public async Task<ActionResult<SalesBootstrap>> Bootstrap() => Ok(await _sales.BootstrapAsync(User.UserId()));

    [HttpPost("attendance")]
    public async Task<ActionResult<AttendanceResult>> Attendance([FromBody] AttendanceRequest req) =>
        Ok(await _sales.MarkAttendanceAsync(User.UserId(), req));

    [HttpPost("visits")]
    public async Task<ActionResult<VisitResult>> Visit([FromBody] VisitRequest req) =>
        Ok(await _sales.SaveVisitAsync(User.UserId(), req));


    /// <summary>Salesperson adds a new shop on one of their own beats.</summary>
    [HttpPost("parties")]
    public async Task<ActionResult<Party>> AddParty([FromBody] NewPartyRequest req) =>
        Ok(await _sales.AddPartyAsync(User.UserId(), req));
}