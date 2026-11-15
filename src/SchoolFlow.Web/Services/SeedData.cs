using SchoolFlow.Core;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>Adds example workflows to an empty database so there is something to explore and copy.</summary>
public static class SeedData
{
    public static void EnsureStarterWorkflows(AppDbContext db)
    {
        if (db.Workflows.Any()) return;
        Add(db, "Technology Purchase Request", "Request hardware or software. Totals are calculated, and larger purchases need approval.", "Purchasing", "indigo", Purchase, publish: true);
        Add(db, "Device Repair Request", "Report a damaged or faulty school device and get a help desk ticket.", "Help Desk", "teal", Repair, publish: true);
        Add(db, "Escalate urgent tickets (example)", "Ticket-triggered example: adds a private note when an urgent ticket arrives. Unpublished — open it in the builder to see how it works.", "Help Desk", "rose", Escalate, publish: false);
        db.SaveChanges();
    }

    private static void Add(AppDbContext db, string name, string description, string category, string color, string json, bool publish)
    {
        // Round-trip through the model so stored JSON is canonical.
        var def = Json.ParseDefinition(json);
        var canonical = Json.Serialize(def);
        db.Workflows.Add(new Workflow
        {
            Name = name, Description = description, Category = category, Color = color,
            DraftJson = canonical,
            PublishedJson = publish ? canonical : null,
            PublishedVersion = publish ? 1 : 0,
            PublishedAt = publish ? DateTime.UtcNow : null,
            TriggerType = def.Trigger.Type,
            CreatedBy = "system", UpdatedBy = "system",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }

    private const string Purchase = """
    {
      "trigger": { "type": "form" },
      "form": {
        "intro": "Use this form for classroom or office technology purchases. Purchases of $500 or more are routed to an approver before a ticket is opened.",
        "submitLabel": "Submit request",
        "fields": [
          { "key": "title", "label": "What do you need?", "type": "text", "required": true, "placeholder": "e.g. Document cameras for science labs" },
          { "key": "department", "label": "Department", "type": "lookup", "source": "departments", "required": true, "width": "half" },
          { "key": "needed_by", "label": "Needed by", "type": "date", "width": "half", "default": "DATEADD(TODAY(), 14)",
            "rules": [ { "check": "needed_by >= DATEADD(TODAY(), 5, 'weekdays')", "message": "Please allow at least 5 school days." } ] },
          { "key": "kind", "label": "Type of purchase", "type": "radio", "options": ["Hardware", "Software", "Both"], "required": true },
          { "key": "license_years", "label": "License length (years)", "type": "number", "min": 1, "max": 5, "decimals": 0,
            "visibleWhen": "IN(kind, 'Software', 'Both')", "required": true, "help": "Software lines are multiplied by the number of years." },
          { "key": "student_data", "label": "This software will store student information", "type": "checkbox", "visibleWhen": "IN(kind, 'Software', 'Both')" },
          { "key": "privacy_notes", "label": "What student information, and why?", "type": "textarea", "visibleWhen": "student_data", "required": true },
          { "key": "items_heading", "label": "Items", "type": "section", "help": "Add one row per item. Line totals and the order total are calculated for you." },
          { "key": "items", "label": "Items to purchase", "type": "table", "required": true, "maxRows": 25,
            "columns": [
              { "key": "item", "label": "Item", "type": "text", "required": true },
              { "key": "category", "label": "Category", "type": "select", "options": ["Hardware", "Software"], "required": true },
              { "key": "qty", "label": "Qty", "type": "number", "required": true },
              { "key": "unit_price", "label": "Unit price", "type": "currency", "required": true },
              { "key": "line_total", "label": "Line total", "type": "calculated", "format": "currency", "decimals": 2,
                "formula": "qty * unit_price * IF(category == 'Software', MAX(license_years, 1), 1)" }
            ] },
          { "key": "subtotal", "label": "Subtotal", "type": "calculated", "format": "currency", "decimals": 2, "formula": "SUM(items.line_total)", "width": "half" },
          { "key": "shipping", "label": "Estimated shipping", "type": "calculated", "format": "currency", "decimals": 2, "width": "half",
            "formula": "IF(SUMIF(items.category, 'Hardware', items.line_total) > 0 && subtotal < 250, 15, 0)", "help": "Flat $15 on hardware orders under $250." },
          { "key": "total", "label": "Order total", "type": "calculated", "format": "currency", "decimals": 2, "formula": "subtotal + shipping" },
          { "key": "approver_email", "label": "Approver's email", "type": "email", "visibleWhen": "total >= 500", "required": true,
            "help": "Orders of $500 or more need approval from your supervisor or principal.",
            "rules": [ { "check": "LOWER(approver_email) != LOWER(submitter.email)", "message": "You cannot approve your own request." } ] },
          { "key": "justification", "label": "Justification", "type": "textarea", "requiredWhen": "total >= 500", "help": "Required for orders of $500 or more." }
        ]
      },
      "flow": {
        "nodes": [
          { "id": "start", "type": "start", "label": "Form submitted", "x": 400, "y": 40 },
          { "id": "needs_approval", "type": "condition", "label": "$500 or more?", "x": 400, "y": 150, "config": { "expression": "total >= 500" } },
          { "id": "approval", "type": "approval", "label": "Supervisor approval", "x": 100, "y": 290,
            "config": { "approvers": "{{approver_email}}", "instructions": "{{submitter.name}} is requesting “{{title}}” for {{department.name}}.\nOrder total: {{TEXT(total, 'C2')}}\n\n{{justification}}" } },
          { "id": "create", "type": "fs_create_ticket", "label": "Open purchasing ticket", "x": 400, "y": 440,
            "config": {
              "subject": "Purchase request: {{title}} ({{TEXT(total, 'C2')}})",
              "description": "Requested by: {{submitter.name}} ({{submitter.email}})\nDepartment: {{department.name}}\nNeeded by: {{TEXT(needed_by, 'MMM d, yyyy')}}\nType: {{kind}}\n\nItems:\n{{JOIN(items.item, ', ')}}\n\nSubtotal: {{TEXT(subtotal, 'C2')}}\nShipping: {{TEXT(shipping, 'C2')}}\nTotal: {{TEXT(total, 'C2')}}\n\nJustification: {{justification}}\nStudent data: {{IF(student_data, 'Yes — ' & privacy_notes, 'No')}}",
              "priority": "2", "priorityFormula": "IF(DATEDIFF(TODAY(), needed_by, 'weekdays') <= 7, 3, 2)", "status": "2",
              "departmentId": "{{department}}", "tags": "purchase-request, {{LOWER(kind)}}" } },
          { "id": "note", "type": "fs_add_note", "label": "Record approval", "x": 100, "y": 600,
            "config": { "body": "Approved by {{approval.by}}.\n{{approval.comment}}", "visibility": "private" } },
          { "id": "was_approved", "type": "condition", "label": "Was approval needed?", "x": 400, "y": 600, "config": { "expression": "NOT(ISBLANK(approval.by))" } },
          { "id": "wait", "type": "wait_ticket", "label": "Wait until resolved", "x": 400, "y": 760, "config": { "until": "ticket.status >= 4" } },
          { "id": "done", "type": "end", "label": "Purchase complete", "x": 400, "y": 890 },
          { "id": "declined", "type": "end", "label": "Not approved", "x": 100, "y": 450, "config": { "outcome": "rejected" } }
        ],
        "edges": [
          { "from": "start", "port": "next", "to": "needs_approval" },
          { "from": "needs_approval", "port": "true", "to": "approval" },
          { "from": "needs_approval", "port": "false", "to": "create" },
          { "from": "approval", "port": "approved", "to": "create" },
          { "from": "approval", "port": "rejected", "to": "declined" },
          { "from": "create", "port": "next", "to": "was_approved" },
          { "from": "was_approved", "port": "true", "to": "note" },
          { "from": "was_approved", "port": "false", "to": "wait" },
          { "from": "note", "port": "next", "to": "wait" },
          { "from": "wait", "port": "next", "to": "done" }
        ]
      }
    }
    """;

    private const string Repair = """
    {
      "trigger": { "type": "form" },
      "form": {
        "intro": "Tell us what happened and we will open a help desk ticket for you.",
        "submitLabel": "Report problem",
        "fields": [
          { "key": "device", "label": "Which device?", "type": "lookup", "source": "my_assets", "help": "Devices assigned to you in Freshservice. Leave blank if yours is not listed." },
          { "key": "device_other", "label": "Device name or asset tag", "type": "text", "visibleWhen": "ISBLANK(device)", "required": true },
          { "key": "problem", "label": "What is wrong?", "type": "select", "required": true,
            "options": ["Cracked screen", "Will not turn on", "Keyboard or trackpad", "Battery or charging", "Liquid damage", "Lost or stolen", "Something else"] },
          { "key": "happened_on", "label": "When did it happen?", "type": "date", "width": "half", "default": "TODAY()",
            "rules": [ { "check": "happened_on <= TODAY()", "message": "This date cannot be in the future." } ] },
          { "key": "location", "label": "Where can we pick it up?", "type": "lookup", "source": "locations", "width": "half", "visibleWhen": "problem != 'Lost or stolen'" },
          { "key": "police_report", "label": "A police report has been filed", "type": "checkbox", "visibleWhen": "problem == 'Lost or stolen'" },
          { "key": "details", "label": "Details", "type": "textarea", "requiredWhen": "IN(problem, 'Something else', 'Liquid damage', 'Lost or stolen')",
            "placeholder": "Anything that helps us fix it faster" },
          { "key": "days_ago", "label": "Days since it happened", "type": "calculated", "formula": "DATEDIFF(happened_on, TODAY())", "visibleWhen": "DATEDIFF(happened_on, TODAY()) > 7",
            "help": "Reports more than a week old are flagged for follow-up." },
          { "key": "needs_loaner", "label": "I need a loaner device", "type": "checkbox" }
        ]
      },
      "flow": {
        "nodes": [
          { "id": "start", "type": "start", "label": "Form submitted", "x": 300, "y": 40 },
          { "id": "urgency", "type": "set_variable", "label": "Work out priority", "x": 300, "y": 150,
            "config": { "name": "priority_level", "expression": "IFS(problem == 'Lost or stolen', 4, problem == 'Liquid damage' || needs_loaner, 3, 2)" } },
          { "id": "create", "type": "fs_create_ticket", "label": "Open help desk ticket", "x": 300, "y": 290,
            "config": {
              "subject": "{{problem}}: {{COALESCE(device.name, device_other)}}",
              "description": "Reported by: {{submitter.name}} ({{submitter.email}})\nDevice: {{COALESCE(device.name, device_other)}}\nProblem: {{problem}}\nHappened on: {{TEXT(happened_on, 'MMM d, yyyy')}}\nPick-up location: {{location.name}}\nLoaner needed: {{IF(needs_loaner, 'Yes', 'No')}}\n\n{{details}}",
              "priority": "2", "priorityFormula": "priority_level", "status": "2", "tags": "device-repair" } },
          { "id": "is_lost", "type": "condition", "label": "Lost or stolen?", "x": 300, "y": 440, "config": { "expression": "problem == 'Lost or stolen'" } },
          { "id": "lost_note", "type": "fs_add_note", "label": "Flag for security", "x": 60, "y": 580,
            "config": { "body": "Device reported lost or stolen. Police report filed: {{IF(police_report, 'Yes', 'No')}}. Lock the device in the management console.", "visibility": "private" } },
          { "id": "done", "type": "end", "label": "Ticket opened", "x": 300, "y": 720 }
        ],
        "edges": [
          { "from": "start", "port": "next", "to": "urgency" },
          { "from": "urgency", "port": "next", "to": "create" },
          { "from": "create", "port": "next", "to": "is_lost" },
          { "from": "is_lost", "port": "true", "to": "lost_note" },
          { "from": "is_lost", "port": "false", "to": "done" },
          { "from": "lost_note", "port": "next", "to": "done" }
        ]
      }
    }
    """;

    private const string Escalate = """
    {
      "trigger": { "type": "ticket", "condition": "ticket.priority >= 4" },
      "form": { "fields": [] },
      "flow": {
        "nodes": [
          { "id": "start", "type": "start", "label": "Ticket event received", "x": 300, "y": 40 },
          { "id": "note", "type": "fs_add_note", "label": "Add escalation note", "x": 300, "y": 170,
            "config": { "body": "Urgent ticket received {{TEXT(NOW(), 'MMM d, h:mm tt')}}. Escalation workflow started.", "visibility": "private" } },
          { "id": "wait", "type": "wait_ticket", "label": "Wait until resolved", "x": 300, "y": 310, "config": { "until": "ticket.status >= 4" } },
          { "id": "done", "type": "end", "label": "Resolved", "x": 300, "y": 440 }
        ],
        "edges": [
          { "from": "start", "port": "next", "to": "note" },
          { "from": "note", "port": "next", "to": "wait" },
          { "from": "wait", "port": "next", "to": "done" }
        ]
      }
    }
    """;
}
