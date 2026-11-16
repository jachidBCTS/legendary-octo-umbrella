using Microsoft.AspNetCore.Mvc;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Requests;

/// <summary>A clean, printable record of one request. Same access rule and same data as the request page.</summary>
public class PrintModel : DetailModel
{
    public PrintModel(AppDbContext db, RunService runs, IFreshserviceClient freshservice, IConfiguration config, AccessService access, AuditService audit)
        : base(db, runs, freshservice, config, access, audit) { }
}
