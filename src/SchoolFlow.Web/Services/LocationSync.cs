using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

public sealed record LocationSyncResult(int Added, int SwitchedOn, int SwitchedOff, int Total, string? Skipped = null, string? Field = null)
{
    public string Describe() => Skipped ?? (Added + SwitchedOn + SwitchedOff == 0
        ? $"Already up to date ({Total} location{(Total == 1 ? "" : "s")} in the ticket form's {Field} list)."
        : $"Synced {Total} location{(Total == 1 ? "" : "s")} from the ticket form's {Field} list: {Added} added, {SwitchedOn} switched on, {SwitchedOff} switched off.");
}

/// <summary>
/// Makes the Locations list the choices of the Freshservice <b>ticket form's Location field</b> (owner, Nov 20,
/// 2026): that list is what the help desk's routing uses. Freshservice's own Locations table is out of date and
/// is not read. Every location not in the list is switched off (never deleted: requests and drafts may use it).
/// </summary>
public static class LocationSync
{
    public const string DefaultField = "Location";

    /// <summary>The ticket form field holding the locations: a field with choices whose label or name is <paramref name="label"/>; a custom field first.</summary>
    public static TicketField? FieldOf(IReadOnlyList<TicketField> fields, string label)
    {
        var wanted = FreshserviceClient.Simple(label);
        return fields.Where(f => f.Choices.Count > 0 && (FreshserviceClient.Simple(f.Label) == wanted || FreshserviceClient.Simple(f.Name) == wanted))
            .OrderBy(f => f.IsDefault).FirstOrDefault();
    }

    public static async Task<LocationSyncResult> RunAsync(AppDbContext db, IFreshserviceClient freshservice, CancellationToken ct, string? fieldLabel = null)
    {
        var label = string.IsNullOrWhiteSpace(fieldLabel) ? DefaultField : fieldLabel.Trim();
        var field = FieldOf(await freshservice.TicketFieldsAsync(ct), label);
        if (field is null)
            return new LocationSyncResult(0, 0, 0, 0, $"The Freshservice ticket form has no “{label}” field with a list of choices, so nothing was changed. "
                + "Check Admin → Settings → Show ticket fields (the field's name can be set with Freshservice:LocationField in the settings file).");
        var names = field.Choices.Select(c => c.Trim()).Where(c => c.Length is > 0 and <= 200)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
            return new LocationSyncResult(0, 0, 0, 0, $"The ticket form's “{field.Label}” field has no choices, so nothing was changed.");

        var rows = await db.Locations.ToListAsync(ct);
        var now = DateTime.UtcNow;
        int added = 0, switchedOn = 0, switchedOff = 0;
        var kept = new HashSet<Location>();
        foreach (var name in names)
        {
            // The same name already here (any spelling of capitals): use it, preferring one that is on.
            var row = rows.Where(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => l.TicketChoice).ThenByDescending(l => l.IsActive).FirstOrDefault();
            if (row is null)
            {
                row = new Location { Name = name, TicketChoice = true, IsActive = true, UpdatedAt = now };
                db.Locations.Add(row);
                rows.Add(row);
                added++;
            }
            else
            {
                var touched = false;
                if (row.Name != name) { row.Name = name; touched = true; }
                // Newly part of the list: offered. One already in the list that an admin switched off stays off.
                if (!row.TicketChoice) { row.TicketChoice = true; row.FreshserviceId = null; touched = true; if (!row.IsActive) { row.IsActive = true; switchedOn++; } }
                if (touched) row.UpdatedAt = now;
            }
            kept.Add(row);
        }
        foreach (var row in rows.Where(l => !kept.Contains(l) && (l.IsActive || l.TicketChoice)))
        {
            if (row.IsActive) switchedOff++;
            row.IsActive = false;
            row.TicketChoice = false;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return new LocationSyncResult(added, switchedOn, switchedOff, names.Count, Field: field.Label);
    }
}
