using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>Chain templates and departments for the workflow engine, read from the database.</summary>
public sealed class ApprovalDirectory : IApprovalDirectory
{
    private readonly AppDbContext _db;
    public ApprovalDirectory(AppDbContext db) => _db = db;

    public async Task<List<ChainStep>?> GetChainAsync(long chainId, CancellationToken ct = default)
    {
        var json = await _db.ApprovalChains.AsNoTracking().Where(c => c.Id == chainId).Select(c => c.StepsJson).FirstOrDefaultAsync(ct);
        return json is null ? null : ChainValue.ToSteps(Json.ToValues("{\"steps\":" + json + "}"));
    }

    public async Task<DepartmentInfo?> GetDepartmentAsync(long departmentId, CancellationToken ct = default)
    {
        var d = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == departmentId, ct);
        return d is null ? null : new DepartmentInfo(d.Id, d.Name, d.FinalApproverList, d.FinalRule);
    }
}
