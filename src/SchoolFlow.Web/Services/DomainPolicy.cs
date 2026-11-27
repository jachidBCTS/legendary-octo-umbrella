namespace SchoolFlow.Web.Services;

/// <summary>
/// The email domains staff can be chosen from where a requester or staff member types an address in
/// themselves (approvers on their own approval chain, a stand-in). Set with <c>Auth:AllowedDomains</c>,
/// e.g. ["bergen.org"]; when it is empty any address is accepted. Builders' "Send notification" steps are
/// not limited, because sending to outside addresses was asked for.
/// </summary>
public sealed class DomainPolicy
{
    public IReadOnlyList<string> Domains { get; }

    public DomainPolicy(IConfiguration config) =>
        Domains = (config.GetSection("Auth:AllowedDomains").Get<string[]>() ?? Array.Empty<string>())
            .Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToList();

    public bool IsAllowed(string email)
    {
        if (Domains.Count == 0) return true;
        var at = email.LastIndexOf('@');
        return at > 0 && Domains.Contains(email[(at + 1)..].Trim().ToLowerInvariant());
    }

    public string Describe() => string.Join(" or ", Domains.Select(d => "@" + d));
}
