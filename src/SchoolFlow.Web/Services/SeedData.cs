using SchoolFlow.Core;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>Adds example workflows to an empty database so there is something to explore and copy.</summary>
public static class SeedData
{
    /// <param name="sampleDepartments">Also add example departments (development only; real ones are set by admins).</param>
    public static void EnsureStarterWorkflows(AppDbContext db, bool sampleDepartments = false)
    {
        if (db.Workflows.Any()) return;
        if (sampleDepartments && !db.Departments.Any())
        {
            db.Departments.AddRange(
                new Department { Name = "Science", FinalApprovers = ApprovalTask.Pack(new[] { "principal@example.org" }) },
                new Department { Name = "Mathematics", FinalApprovers = ApprovalTask.Pack(new[] { "principal@example.org", "admin@example.org" }), FinalRule = "all" },
                new Department { Name = "Athletics", FinalApprovers = ApprovalTask.Pack(new[] { "admin@example.org" }) });
        }
        if (sampleDepartments && !db.DirectoryGroups.Any())
        {
            // Match the made-up group IDs the development sign-in gives its sample people.
            db.DirectoryGroups.AddRange(
                new DirectoryGroup { Name = "All Staff", ObjectId = "00000000-0000-0000-0000-000000000001" },
                new DirectoryGroup { Name = "Science Department", ObjectId = "00000000-0000-0000-0000-000000000002" },
                new DirectoryGroup { Name = "Workflow Builders", ObjectId = "00000000-0000-0000-0000-000000000003", Role = Roles.Builder });
        }
        if (sampleDepartments && !db.Locations.Any())
        {
            foreach (var name in new[] { "Main Building", "Main Building › Library", "Gymnasium" })
                db.Locations.Add(new Location { Name = name, UpdatedAt = DateTime.UtcNow });
        }
        Add(db, "Technology Purchase Request", "Request hardware or software. Totals are calculated, and larger purchases need approval.", "Purchasing", "indigo", Purchase, publish: true);
        Add(db, "Device Repair Request", "Report a damaged or faulty school device and get a help desk ticket.", "Help Desk", "teal", Repair, publish: true);
        Add(db, "Escalate urgent tickets (example)", "Ticket-triggered example: adds a private note when an urgent ticket arrives. Unpublished — open it in the builder to see how it works.", "Help Desk", "rose", Escalate, publish: false);
        // Its bus booking goes to the Transportation team, which a real site fills in first (Admin → Teams).
        Add(db, "Field Trip Request", "Plan a trip, choose who signs it off, and reuse last year's request as a starting point.", "Trips", "green", FieldTrip, publish: sampleDepartments);
        // The approvers of the travel form are real people the school must name. With the example
        // people (development) it is published; otherwise it stays a draft until an admin fills them in.
        // Rates the district keeps (Admin → Rates). Each is added once, if missing, so an existing site
        // gains new ones on its next start. Mileage starts at 0 on a real site (the district sets it, and the
        // form refuses to cost miles until then). The GSA standard rates are only a fallback for when
        // gsa.gov cannot be reached; they start at GSA's published standard amounts.
        foreach (var (key, label, example, real) in new[]
                 {
                     ("mileage", "Mileage rate (per mile)", 0.47m, 0m),
                     (PerDiemService.StandardMealsKey, "GSA fallback: meals a day", 68m, 68m),
                     (PerDiemService.StandardLodgingKey, "GSA fallback: hotel a night", 110m, 110m),
                 })
            if (!db.Rates.Any(r => r.Key == key))
                db.Rates.Add(new Rate { Key = key, Label = label, Value = sampleDepartments ? example : real, UpdatedAt = DateTime.UtcNow });
        if (!db.Teams.Any())
        {
            // The people behind the travel form's steps. Real names are the district's to enter (Admin →
            // Teams), so a new site starts with empty teams and the travel form stays a draft until then.
            foreach (var (name, example) in StarterTeams)
                db.Teams.Add(new Team { Name = name, Members = sampleDepartments ? ApprovalTask.Pack(new[] { example }) : "", UpdatedAt = DateTime.UtcNow });
        }
        Add(db, "Request for Work Related Travel", "Conference, workshop or meeting travel, with the estimated cost worksheet.", "Travel", "blue",
            Travel, publish: sampleDepartments);
        db.SaveChanges();
    }

    /// <summary>The teams the starter forms name, with the development sign-in's example person for each.</summary>
    public static readonly (string Name, string Example)[] StarterTeams =
    {
        ("Transportation", "admin@example.org"),
        ("Central Office Administrator (Technical Schools)", "builder@example.org"),
        ("Central Office Administrator (Special Services)", "admin@example.org"),
        ("Business Administrator", "admin@example.org"),
        ("Superintendent", "principal@example.org"),
        ("Business Office (Technical Schools)", "admin@example.org"),
        ("Business Office (Special Services)", "builder@example.org"),
    };

    /// <summary>The starter definitions, exposed so tests can check they stay valid.</summary>
    public static IReadOnlyList<(string Name, string Json)> Templates =>
        new[] { ("Purchase", Purchase), ("Repair", Repair), ("Escalate", Escalate), ("FieldTrip", FieldTrip), ("Travel", Travel) };

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
          { "key": "justification", "label": "Justification", "type": "textarea", "requiredWhen": "total >= 500", "help": "Required for orders of $500 or more." },
          { "key": "quotes", "label": "Quotes or supporting documents", "type": "attachments", "maxFiles": 5, "requiredWhen": "total >= 2500",
            "help": "Attach the vendor quote. Required for orders of $2,500 or more." }
        ]
      },
      "flow": {
        "nodes": [
          { "id": "start", "type": "start", "label": "Form submitted", "x": 290, "y": 40 },
          { "id": "needs_approval", "type": "condition", "label": "$500 or more?", "x": 290, "y": 150, "config": { "expression": "total >= 500" } },
          { "id": "approval", "type": "approval", "label": "Supervisor approval", "x": 30, "y": 290,
            "config": { "approvers": "{{approver_email}}", "instructions": "{{submitter.name}} is requesting “{{title}}” for {{department.name}}.\nOrder total: {{TEXT(total, 'C2')}}\n\n{{justification}}" } },
          { "id": "create", "type": "fs_create_ticket", "label": "Open purchasing ticket", "x": 290, "y": 440,
            "config": {
              "subject": "Purchase request: {{title}} ({{TEXT(total, 'C2')}})",
              "description": "Requested by: {{submitter.name}} ({{submitter.email}})\nDepartment: {{department.name}}\nNeeded by: {{TEXT(needed_by, 'MMM d, yyyy')}}\nType: {{kind}}\n\nItems:\n{{JOIN(items.item, ', ')}}\n\nSubtotal: {{TEXT(subtotal, 'C2')}}\nShipping: {{TEXT(shipping, 'C2')}}\nTotal: {{TEXT(total, 'C2')}}\n\nJustification: {{justification}}\nStudent data: {{IF(student_data, 'Yes — ' & privacy_notes, 'No')}}",
              "priority": "2", "priorityFormula": "IF(DATEDIFF(TODAY(), needed_by, 'weekdays') <= 7, 3, 2)", "status": "2",
              "departmentId": "{{department}}", "tags": "purchase-request, {{LOWER(kind)}}", "attachFiles": "true" } },
          { "id": "note", "type": "fs_add_note", "label": "Record approval", "x": 30, "y": 750,
            "config": { "body": "Approved by {{approval.by}}.\n{{approval.comment}}", "visibility": "private" } },
          { "id": "was_approved", "type": "condition", "label": "Was approval needed?", "x": 290, "y": 600, "config": { "expression": "NOT(ISBLANK(approval.by))" } },
          { "id": "wait", "type": "wait_ticket", "label": "Wait until resolved", "x": 290, "y": 890, "config": { "until": "IN(ticket.status, 4, 5)" } },
          { "id": "done", "type": "end", "label": "Purchase complete", "x": 290, "y": 1020 },
          { "id": "declined", "type": "end", "label": "Not approved", "x": 30, "y": 450, "config": { "outcome": "rejected" } }
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
          { "key": "location", "label": "Where can we pick it up?", "type": "location", "width": "half", "visibleWhen": "problem != 'Lost or stolen'" },
          { "key": "police_report", "label": "A police report has been filed", "type": "checkbox", "visibleWhen": "problem == 'Lost or stolen'" },
          { "key": "details", "label": "Details", "type": "textarea", "requiredWhen": "IN(problem, 'Something else', 'Liquid damage', 'Lost or stolen')",
            "placeholder": "Anything that helps us fix it faster" },
          { "key": "days_ago", "label": "Days since it happened", "type": "calculated", "formula": "DATEDIFF(happened_on, TODAY())", "visibleWhen": "DATEDIFF(happened_on, TODAY()) > 7",
            "help": "Reports more than a week old are flagged for follow-up." },
          { "key": "needs_loaner", "label": "I need a loaner device", "type": "checkbox" },
          { "key": "photos", "label": "Photos of the problem", "type": "attachments", "maxFiles": 4, "help": "Optional. A photo of the damage or of the error on screen helps us fix it faster." }
        ]
      },
      "flow": {
        "nodes": [
          { "id": "start", "type": "start", "label": "Form submitted", "x": 290, "y": 40 },
          { "id": "urgency", "type": "set_variable", "label": "Work out priority", "x": 290, "y": 150,
            "config": { "name": "priority_level", "expression": "IFS(problem == 'Lost or stolen', 4, problem == 'Liquid damage' || needs_loaner, 3, 2)" } },
          { "id": "create", "type": "fs_create_ticket", "label": "Open help desk ticket", "x": 290, "y": 290,
            "config": {
              "subject": "{{problem}}: {{COALESCE(device.name, device_other)}}",
              "description": "Reported by: {{submitter.name}} ({{submitter.email}})\nDevice: {{COALESCE(device.name, device_other)}}\nProblem: {{problem}}\nHappened on: {{TEXT(happened_on, 'MMM d, yyyy')}}\nPick-up location: {{location.name}}\nLoaner needed: {{IF(needs_loaner, 'Yes', 'No')}}\n\n{{details}}",
              "priority": "2", "priorityFormula": "priority_level", "status": "2", "tags": "device-repair", "attachFiles": "true" } },
          { "id": "is_lost", "type": "condition", "label": "Lost or stolen?", "x": 290, "y": 440, "config": { "expression": "problem == 'Lost or stolen'" } },
          { "id": "lost_note", "type": "fs_add_note", "label": "Flag for security", "x": 30, "y": 580,
            "config": { "body": "Device reported lost or stolen. Police report filed: {{IF(police_report, 'Yes', 'No')}}. Lock the device in the management console.", "visibility": "private" } },
          { "id": "done", "type": "end", "label": "Ticket opened", "x": 290, "y": 720 }
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

    private const string FieldTrip = """
    {
      "trigger": { "type": "form" },
      "form": {
        "allowCopy": "everyone",
        "intro": "Tip: if this trip has run before, use “Start from a previous request” and just update what has changed.",
        "submitLabel": "Send for approval",
        "fields": [
          { "key": "destination", "label": "Where are you going?", "type": "text", "required": true, "placeholder": "e.g. Liberty Science Center" },
          { "key": "department", "label": "Department", "type": "department", "required": true, "width": "half", "help": "Decides who gives final approval." },
          { "key": "trip_date", "label": "Date of trip", "type": "date", "required": true, "width": "half", "noCopy": true,
            "rules": [ { "check": "trip_date >= DATEADD(TODAY(), 10, 'weekdays')", "message": "Trips need at least 10 school days' notice." } ] },
          { "key": "students", "label": "Number of students", "type": "number", "required": true, "min": 1, "decimals": 0, "width": "half" },
          { "key": "chaperones", "label": "Number of chaperones", "type": "number", "required": true, "min": 1, "decimals": 0, "width": "half",
            "rules": [ { "check": "students / chaperones <= 10", "message": "You need at least one chaperone for every 10 students." } ] },
          { "key": "ratio", "label": "Students per chaperone", "type": "calculated", "formula": "IF(chaperones > 0, ROUND(students / chaperones, 1), blank)", "width": "half" },
          { "key": "cost_per_student", "label": "Cost per student", "type": "currency", "min": 0, "width": "half" },
          { "key": "bus_needed", "label": "We need a bus", "type": "checkbox" },
          { "key": "bus_cost", "label": "Bus quote", "type": "currency", "visibleWhen": "bus_needed", "required": true, "min": 0 },
          { "key": "total_cost", "label": "Total cost", "type": "calculated", "format": "currency", "decimals": 2, "formula": "students * cost_per_student + bus_cost" },
          { "key": "purpose", "label": "How does this trip support the curriculum?", "type": "textarea", "required": true },
          { "key": "approvers", "label": "Who needs to approve this first?", "type": "approval_chain",
            "help": "Optional. For example your team leader, then the nurse." }
        ]
      },
      "flow": {
        "nodes": [
          { "id": "start", "type": "start", "label": "Form submitted", "x": 290, "y": 40 },
          { "id": "signoff", "type": "approval_chain", "label": "Trip sign-off", "x": 290, "y": 150,
            "config": { "chainSource": "field", "chainField": "approvers", "departmentSource": "field", "departmentField": "department",
              "instructions": "{{submitter.name}} is planning a trip to {{destination}} on {{TEXT(trip_date, 'MMM d, yyyy')}} for {{students}} students with {{chaperones}} chaperones.\nTotal cost: {{TEXT(total_cost, 'C2')}}\n\n{{purpose}}" } },
          { "id": "needs_bus", "type": "condition", "label": "Bus needed?", "x": 290, "y": 300, "config": { "expression": "bus_needed" } },
          { "id": "book_bus", "type": "task", "label": "Transportation: book the bus", "x": 30, "y": 440,
            "config": { "assignees": "[Transportation]",
              "instructions": "Please book a bus for {{submitter.name}}'s trip to {{destination}} on {{TEXT(trip_date, 'dddd, MMM d, yyyy')}}.\nStudents: {{students}}, chaperones: {{chaperones}}. Department: {{department.name}}.\nQuote given on the request: {{TEXT(bus_cost, 'C2')}}\n\n{{purpose}}" },
            "fields": [
              { "key": "bus_company", "label": "Bus company", "type": "text", "required": true },
              { "key": "pickup_time", "label": "Pick-up time", "type": "time", "required": true },
              { "key": "bus_confirmation", "label": "Confirmation number", "type": "text" },
              { "key": "bus_final_cost", "label": "Final cost", "type": "currency", "min": 0, "default": "bus_cost" }
            ] },
          { "id": "tell", "type": "notify", "label": "Tell the organiser", "x": 290, "y": 590,
            "config": { "to": "{{submitter.email}}", "subject": "Your trip to {{destination}} is approved",
              "message": "Final approval was given by {{approval.by}}.\n{{IF(bus_needed, 'Your bus is booked with ' & bus_company & ', picking up at ' & pickup_time & IF(ISBLANK(bus_confirmation), '.', ' (confirmation ' & bus_confirmation & ').'), 'No bus was requested.')}}" } },
          { "id": "done", "type": "end", "label": "Trip approved", "x": 290, "y": 730 },
          { "id": "declined", "type": "end", "label": "Trip not approved", "x": 580, "y": 300, "config": { "outcome": "rejected" } }
        ],
        "edges": [
          { "from": "start", "port": "next", "to": "signoff" },
          { "from": "signoff", "port": "approved", "to": "needs_bus" },
          { "from": "signoff", "port": "rejected", "to": "declined" },
          { "from": "needs_bus", "port": "true", "to": "book_bus" },
          { "from": "needs_bus", "port": "false", "to": "tell" },
          { "from": "book_bus", "port": "next", "to": "tell" },
          { "from": "tell", "port": "next", "to": "done" }
        ]
      }
    }
    """;

    /// <summary>
    /// The district's paper "Request for Work Related Travel" form (revised July 2018): page 1 and
    /// the estimated cost worksheet. The signature blocks of the paper form are the approval steps.
    /// </summary>
    private const string Travel = """
    {
      "trigger": { "type": "form" },
      "form": {
        "style": "paper",
        "allowCopy": "own",
        "intro": "• All travel must be approved by the Superintendent of Schools\n• Travel with a cost of $150 or more must be approved by the Board of Education\n• Travel arrangements, including registrations, to be made after all approvals are received\n• Staff is expected to turnkey professional development information to other staff",
        "submitLabel": "Submit for approval",
        "fields": [
          { "key": "district", "label": "District", "type": "radio", "options": ["Bergen County Technical Schools", "Bergen County Special Services"], "required": true },
          { "key": "request_date", "label": "Date of Request", "type": "date", "default": "TODAY()", "required": true, "width": "half", "noCopy": true },
          { "key": "budget_account", "label": "Budget Acct. #", "type": "text", "width": "half" },
          { "key": "employee_name", "label": "Employee Name", "type": "text", "default": "submitter.name", "required": true, "width": "half" },
          { "key": "po_number", "label": "PO #", "type": "text", "width": "half", "noCopy": true },
          { "key": "department", "label": "School/Department", "type": "department", "required": true, "width": "half" },
          { "key": "students", "label": "# of Students", "type": "number", "min": 0, "decimals": 0, "width": "half" },
          { "key": "event_title", "label": "Meeting/Conference Title", "type": "text", "required": true },
          { "key": "location", "label": "Location (full address)", "type": "textarea", "required": true },
          { "key": "dest_zip", "label": "Destination ZIP code", "type": "text", "required": true, "width": "half", "placeholder": "e.g. 08540",
            "help": "Used to look up the GSA meal rates and hotel limit for where you are going.",
            "rules": [ { "check": "MATCHES(TRIM(dest_zip), '^[0-9]{5}(-[0-9]{4})?$')", "message": "Enter a 5-digit US ZIP code." } ] },
          { "key": "from_date", "label": "From date", "type": "date", "required": true, "width": "half", "noCopy": true },
          { "key": "from_time", "label": "From time", "type": "time", "width": "half", "noCopy": true },
          { "key": "to_date", "label": "To date", "type": "date", "required": true, "width": "half", "noCopy": true,
            "rules": [ { "check": "to_date >= from_date", "message": "The return date is before the start date." } ] },
          { "key": "to_time", "label": "To time", "type": "time", "width": "half", "noCopy": true },
          { "key": "gsa", "label": "GSA per diem rates", "type": "perdiem", "perDiem": { "zip": "dest_zip", "from": "from_date", "to": "to_date" },
            "requiredWhen": "meal_first_people > 0 || meal_days > 0 || meal_last_people > 0 || lodging_nights > 0 || lodging2_nights > 0",
            "help": "Looked up from gsa.gov for the destination ZIP code and your dates. The meal rates below come from here, and the hotel rate cannot be more than the limit unless it is the conference hotel." },
          { "key": "paid_by", "label": "Cost to be paid by", "type": "multiselect", "options": ["BOE", "Students", "Sponsor"], "required": true, "width": "half" },
          { "key": "sponsor_name", "label": "Name of Sponsor", "type": "text", "visibleWhen": "CONTAINS(paid_by, 'Sponsor')", "required": true, "width": "half" },
          { "key": "other_staff", "label": "1. List names of other staff attending the event", "type": "textarea",
            "help": "Are all staff requesting the same reimbursement amount or no reimbursement? If so, complete only one travel request for all staff and list every staff member here or on an attachment. If different reimbursement amounts are being requested, complete a separate travel request for each amount." },
          { "key": "annual_heading", "label": "2. Annual Event", "type": "section",
            "help": "If this conference, convention, workshop, etc. is an annual event and you attended the event last year, complete the following." },
          { "key": "last_year_date", "label": "Date of last year's event", "type": "date", "width": "half" },
          { "key": "last_year_cost", "label": "Total Cost Last Year", "type": "currency", "min": 0, "width": "half" },
          { "key": "reasons_heading", "label": "3. Reasons for Attending", "type": "section",
            "help": "Objectives of and/or information to be gained at the conference. Must be completed." },
          { "key": "reason_a", "label": "a.", "type": "text", "required": true },
          { "key": "reason_b", "label": "b.", "type": "text" },
          { "key": "reason_c", "label": "c.", "type": "text" },

          { "key": "cost_page", "label": "Estimated Cost Worksheet", "type": "page" },
          { "key": "worksheet", "label": "Estimated Cost Worksheet", "type": "worksheet",
            "rows": [
              { "number": "1.", "item": "Transportation: Personal Automobile", "description": "Estimated Mileage: {miles} miles x $ {mileage_rate} / mile", "cost": "mileage_cost" },
              { "item": "Tolls", "description": "Estimated Tolls:", "cost": "tolls" },
              { "item": "Parking", "description": "Parking Fees", "cost": "parking" },
              { "number": "2.", "item": "Other Transportation (Air, Rail, Bus)", "description": "Economy Tickets", "cost": "tickets" },
              { "item": "Baggage Cost", "cost": "baggage" },
              { "number": "3.", "item": "Taxi", "description": "Estimated number of taxi rides {taxi_rides}", "cost": "taxi" },
              { "number": "4.", "item": "Meal Allowance", "description": "1st Day of Meeting/Convention Rate: {meal_first_rate} x {meal_first_people} person(s)", "cost": "meal_first" },
              { "description": "# of Full Meeting Days: {meal_days} x {meal_day_rate} / day x {meal_day_people} person(s)", "cost": "meal_full" },
              { "description": "Last Day Meeting/Convention Rate: {meal_last_rate} x {meal_last_people} person(s)", "cost": "meal_last" },
              { "number": "5.", "item": "Lodging", "description": "# of Nights at Hotel: {lodging_nights} x {lodging_rate} / night (GSA limit {gsa_hotel_limit}) x {lodging_rooms} room(s)", "cost": "lodging" },
              { "item": "Additional Lodging", "description": "# of Nights at Hotel: {lodging2_nights} x {lodging2_rate} / night x {lodging2_rooms} room(s)", "cost": "lodging2" },
              { "number": "6.", "item": "Per Diem Rate", "description": "{per_diem_rate} x {per_diem_days} day(s)", "cost": "per_diem" },
              { "number": "7.", "item": "Overnight Stipend", "description": "{stipend_rate} x {stipend_nights} night(s)", "cost": "stipend" },
              { "number": "8.", "item": "Miscellaneous Expenses (be specific)", "description": "{misc_description}", "cost": "misc" },
              { "number": "9.", "item": "Registration/Meeting Fee", "description": "Submit a separate purchase order with this form", "cost": "registration" },
              { "description": "Total Estimated Expenses:", "cost": "total" }
            ] },
          { "key": "miles", "label": "Mileage: miles", "type": "number", "min": 0,
            "rules": [ { "check": "miles = 0 || rates.mileage > 0", "message": "The mileage rate has not been set yet. Please ask an administrator." } ] },
          { "key": "mileage_rate", "label": "Mileage: rate per mile", "type": "calculated", "formula": "rates.mileage" },
          { "key": "mileage_cost", "label": "Personal automobile", "type": "calculated", "format": "currency", "decimals": 2, "formula": "ROUND(miles * mileage_rate, 2)" },
          { "key": "tolls", "label": "Tolls", "type": "currency", "min": 0 },
          { "key": "parking", "label": "Parking", "type": "currency", "min": 0 },
          { "key": "tickets", "label": "Other transportation (air, rail, bus)", "type": "currency", "min": 0 },
          { "key": "baggage", "label": "Baggage cost", "type": "currency", "min": 0 },
          { "key": "taxi_rides", "label": "Taxi: number of rides", "type": "number", "min": 0, "decimals": 0 },
          { "key": "taxi", "label": "Taxi", "type": "currency", "min": 0 },
          { "key": "meal_first_rate", "label": "Meals, first day: rate", "type": "calculated", "format": "currency", "decimals": 2, "formula": "gsa.first_last_day" },
          { "key": "meal_first_people", "label": "Meals, first day: persons", "type": "number", "min": 0, "decimals": 0,
            "rules": [ { "check": "meal_first_people = 0 || gsa.meals > 0", "message": "The meal rate is not known yet: enter the destination ZIP code and travel dates above." } ] },
          { "key": "meal_first", "label": "Meals, first day", "type": "calculated", "format": "currency", "decimals": 2, "formula": "ROUND(meal_first_rate * meal_first_people, 2)" },
          { "key": "meal_days", "label": "Meals, full days: number of days", "type": "number", "min": 0, "decimals": 0,
            "rules": [ { "check": "meal_days = 0 || gsa.meals > 0", "message": "The meal rate is not known yet: enter the destination ZIP code and travel dates above." } ] },
          { "key": "meal_day_rate", "label": "Meals, full days: rate per day", "type": "calculated", "format": "currency", "decimals": 2, "formula": "gsa.meals" },
          { "key": "meal_day_people", "label": "Meals, full days: persons", "type": "number", "min": 0, "decimals": 0, "default": "1" },
          { "key": "meal_full", "label": "Meals, full days", "type": "calculated", "format": "currency", "decimals": 2, "formula": "ROUND(meal_days * meal_day_rate * meal_day_people, 2)" },
          { "key": "meal_last_rate", "label": "Meals, last day: rate", "type": "calculated", "format": "currency", "decimals": 2, "formula": "gsa.first_last_day" },
          { "key": "meal_last_people", "label": "Meals, last day: persons", "type": "number", "min": 0, "decimals": 0,
            "rules": [ { "check": "meal_last_people = 0 || gsa.meals > 0", "message": "The meal rate is not known yet: enter the destination ZIP code and travel dates above." } ] },
          { "key": "meal_last", "label": "Meals, last day", "type": "calculated", "format": "currency", "decimals": 2, "formula": "ROUND(meal_last_rate * meal_last_people, 2)" },
          { "key": "lodging_nights", "label": "Lodging: nights", "type": "number", "min": 0, "decimals": 0 },
          { "key": "gsa_hotel_limit", "label": "GSA hotel limit a night", "type": "calculated", "format": "currency", "decimals": 2, "formula": "gsa.lodging" },
          { "key": "lodging_rate", "label": "Lodging: rate per night", "type": "currency", "min": 0,
            "rules": [ { "check": "conference_hotel || ISBLANK(gsa) || NOT(gsa.lodging > 0) || lodging_rate <= gsa.lodging", "message": "This is more than the GSA hotel limit for this destination and these dates. Lower it, or tick “conference hotel” below if it is the conference hotel." } ] },
          { "key": "lodging_rooms", "label": "Lodging: rooms", "type": "number", "min": 0, "decimals": 0, "default": "1" },
          { "key": "lodging", "label": "Lodging", "type": "calculated", "format": "currency", "decimals": 2, "formula": "ROUND(lodging_nights * lodging_rate * lodging_rooms, 2)" },
          { "key": "lodging2_nights", "label": "Additional lodging: nights", "type": "number", "min": 0, "decimals": 0 },
          { "key": "lodging2_rate", "label": "Additional lodging: rate per night", "type": "currency", "min": 0,
            "rules": [ { "check": "conference_hotel || ISBLANK(gsa) || NOT(gsa.lodging > 0) || lodging2_rate <= gsa.lodging", "message": "This is more than the GSA hotel limit for this destination and these dates. Lower it, or tick “conference hotel” below if it is the conference hotel." } ] },
          { "key": "lodging2_rooms", "label": "Additional lodging: rooms", "type": "number", "min": 0, "decimals": 0 },
          { "key": "lodging2", "label": "Additional lodging", "type": "calculated", "format": "currency", "decimals": 2, "formula": "ROUND(lodging2_nights * lodging2_rate * lodging2_rooms, 2)" },
          { "key": "conference_hotel", "label": "My hotel is the conference hotel (or a similar hotel at no more than its rate)", "type": "checkbox",
            "help": "District rules allow the conference hotel's rate even when it is above the GSA limit. Attach documentation of the rate below." },
          { "key": "per_diem_rate", "label": "Per diem: rate", "type": "currency", "min": 0 },
          { "key": "per_diem_days", "label": "Per diem: days", "type": "number", "min": 0, "decimals": 0 },
          { "key": "per_diem", "label": "Per diem", "type": "calculated", "format": "currency", "decimals": 2, "formula": "ROUND(per_diem_rate * per_diem_days, 2)" },
          { "key": "stipend_rate", "label": "Overnight stipend: rate", "type": "currency", "min": 0 },
          { "key": "stipend_nights", "label": "Overnight stipend: nights", "type": "number", "min": 0, "decimals": 0 },
          { "key": "stipend", "label": "Overnight stipend", "type": "calculated", "format": "currency", "decimals": 2, "formula": "ROUND(stipend_rate * stipend_nights, 2)" },
          { "key": "misc_description", "label": "Miscellaneous expenses: what for", "type": "text", "requiredWhen": "misc > 0" },
          { "key": "misc", "label": "Miscellaneous expenses", "type": "currency", "min": 0 },
          { "key": "registration", "label": "Registration/meeting fee", "type": "currency", "min": 0 },
          { "key": "total", "label": "Total Estimated Expenses", "type": "calculated", "format": "currency", "decimals": 2,
            "formula": "mileage_cost + tolls + parking + tickets + baggage + taxi + meal_first + meal_full + meal_last + lodging + lodging2 + per_diem + stipend + misc + registration" },
          { "key": "documents", "label": "Supporting documents", "type": "attachments", "maxFiles": 10, "requiredWhen": "conference_hotel",
            "help": "For example the event flyer or agenda, the list of staff attending, and hotel rate documentation." },
          { "key": "notes", "label": "Notes", "type": "section",
            "help": "Meal rates and the hotel limit follow the Federal OMB (GSA) per diem rates for the destination, which this form looks up from gsa.gov for the ZIP code and dates you enter. Each state and city has a different lodging and M&IE rate; the first and last day of travel are paid at 75% of the daily meals rate. Lodging expenses may exceed the federal per diem rate if the hotel is the site of the conference, seminar, or meeting. If the “conference hotel” is not available, lodging may be paid for similar accommodations at a rate not to exceed the “conference hotel” rate. Kindly attach documentation.\n\nUpon returning from the trip AND in order to be eligible for reimbursement, all employees must file a Professional Development Report with their reimbursement claim. Include key issues that were addressed at the event and its relevance to improving instruction or the operation of the district.\n\nStaff member must reconcile this worksheet with actual expenditures with the Business Office. The reconciled Travel Request Form must be submitted to the Business Office within 3 weeks of the travel. All expenditures must be documented with original itemized receipts to be reimbursed. The Board of Education will only reimburse expenditures up to the maximum permitted by Federal OMB Guidelines." }
        ]
      },
      "flow": {
        "nodes": [
          { "id": "start", "type": "start", "label": "Form submitted", "x": 290, "y": 40 },
          { "id": "program", "type": "approval_chain", "label": "Program approval: Principal/Department Head", "x": 290, "y": 150,
            "config": { "departmentSource": "field", "departmentField": "department",
              "instructions": "{{employee_name}} requests approval to attend “{{event_title}}”, {{TEXT(from_date, 'MMM d, yyyy')}} to {{TEXT(to_date, 'MMM d, yyyy')}}.\nLocation: {{location}}\nTotal estimated expenses: {{TEXT(total, 'C2')}} (paid by {{JOIN(paid_by, ', ')}})\n\nReasons for attending:\n{{reason_a}}\n{{reason_b}}\n{{reason_c}}" } },
          { "id": "central", "type": "approval", "label": "Central Office Administrator approval", "x": 290, "y": 300,
            "config": { "approvers": "{{IF(district == 'Bergen County Technical Schools', '[Central Office Administrator (Technical Schools)]', '[Central Office Administrator (Special Services)]')}}",
              "instructions": "{{employee_name}} ({{district}}): “{{event_title}}”, {{TEXT(from_date, 'MMM d, yyyy')}} to {{TEXT(to_date, 'MMM d, yyyy')}}.\nTotal estimated expenses: {{TEXT(total, 'C2')}}" } },
          { "id": "business", "type": "approval", "label": "Business Administrator approval", "x": 290, "y": 450,
            "config": { "approvers": "[Business Administrator]", "instructions": "{{employee_name}} ({{district}}): “{{event_title}}”.\nBudget account: {{budget_account}}   PO #: {{po_number}}\nTotal estimated expenses: {{TEXT(total, 'C2')}} (paid by {{JOIN(paid_by, ', ')}})" } },
          { "id": "superintendent", "type": "approval", "label": "Superintendent approval", "x": 290, "y": 600,
            "config": { "approvers": "[Superintendent]", "instructions": "{{employee_name}} ({{district}}): “{{event_title}}”, {{TEXT(from_date, 'MMM d, yyyy')}} to {{TEXT(to_date, 'MMM d, yyyy')}}.\nTotal estimated expenses: {{TEXT(total, 'C2')}}" } },
          { "id": "office", "type": "task", "label": "Business office: budget, PO and Board approval", "x": 290, "y": 750,
            "config": { "assignees": "{{IF(district == 'Bergen County Technical Schools', '[Business Office (Technical Schools)]', '[Business Office (Special Services)]')}}",
              "instructions": "{{employee_name}}'s travel to “{{event_title}}” has been approved by the administrators.\nTotal estimated expenses: {{TEXT(total, 'C2')}} (paid by {{JOIN(paid_by, ', ')}})\n{{IF(total >= 150, 'This is $150 or more, so it needs Board of Education approval. Complete this task once the Board has decided.', 'This is under $150, so Board of Education approval is not required.')}}" },
            "fields": [
              { "key": "office_budget", "label": "Confirmed Budget Acct. #", "type": "text", "requiredWhen": "total < 150 || board_decision == 'Approved'", "default": "budget_account" },
              { "key": "office_po", "label": "PO # issued", "type": "text", "default": "po_number" },
              { "key": "board_decision", "label": "Board of Education decision", "type": "radio", "options": ["Approved", "Not approved"], "visibleWhen": "total >= 150", "required": true },
              { "key": "board_date", "label": "Board of Education approval date", "type": "date", "visibleWhen": "board_decision == 'Approved'", "required": true },
              { "key": "office_notes", "label": "Notes", "type": "textarea" }
            ] },
          { "id": "board_ok", "type": "condition", "label": "Cleared to travel?", "x": 290, "y": 900, "config": { "expression": "total < 150 || board_decision == 'Approved'" } },
          { "id": "tell", "type": "notify", "label": "Tell the employee: approved", "x": 290, "y": 1040,
            "config": { "to": "{{submitter.email}}", "subject": "Travel approved: {{event_title}}",
              "message": "All approvals have been received for “{{event_title}}”. Travel arrangements, including registrations, may now be made.\nPO #: {{office_po}}\n\nAfter the trip, open this request and complete “After the trip”: your actual costs, original itemized receipts and your Professional Development Report. This must be done within 3 weeks of the travel to be eligible for reimbursement." } },
          { "id": "after_trip", "type": "task", "label": "After the trip: actual costs and receipts", "x": 290, "y": 1190,
            "config": { "assignees": "{{submitter.email}}", "remindDays": "0", "dueDate": "DATEADD(to_date, 21)",
              "dueNotify": "{{IF(district == 'Bergen County Technical Schools', '[Business Office (Technical Schools)]', '[Business Office (Special Services)]')}}",
              "instructions": "Complete this by {{TEXT(DATEADD(to_date, 21), 'dddd, MMM d, yyyy')}}, 3 weeks after returning from “{{event_title}}” ({{TEXT(to_date, 'MMM d, yyyy')}}). If it is not done by then, you and the business office are reminded.\nYour estimate was {{TEXT(total, 'C2')}}. All expenditures must be documented with original itemized receipts to be reimbursed." },
            "fields": [
              { "key": "actual_total", "label": "Actual total cost", "type": "currency", "required": true, "min": 0 },
              { "key": "receipts", "label": "Original itemized receipts", "type": "attachments", "maxFiles": 20, "requiredWhen": "actual_total > 0" },
              { "key": "pd_report", "label": "Professional Development Report", "type": "attachments", "maxFiles": 5, "required": true },
              { "key": "trip_notes", "label": "Anything the business office should know", "type": "textarea" }
            ] },
          { "id": "reconcile", "type": "task", "label": "Business office: reconcile travel costs", "x": 290, "y": 1340,
            "config": { "assignees": "{{IF(district == 'Bergen County Technical Schools', '[Business Office (Technical Schools)]', '[Business Office (Special Services)]')}}",
              "instructions": "{{employee_name}} has filed actual costs for “{{event_title}}”.\nEstimated: {{TEXT(total, 'C2')}}   Actual: {{TEXT(actual_total, 'C2')}}\nThe receipts and Professional Development Report are on the request. {{trip_notes}}" },
            "fields": [
              { "key": "reimbursement", "label": "Amount to reimburse", "type": "currency", "required": true, "min": 0 },
              { "key": "reconcile_notes", "label": "Notes for the employee", "type": "textarea" }
            ] },
          { "id": "tell_done", "type": "notify", "label": "Tell the employee: reconciled", "x": 290, "y": 1490,
            "config": { "to": "{{submitter.email}}", "subject": "Travel reconciled: {{event_title}}",
              "message": "The business office has reconciled your travel costs.\nAmount to reimburse: {{TEXT(reimbursement, 'C2')}}\n{{reconcile_notes}}" } },
          { "id": "done", "type": "end", "label": "Travel completed", "x": 290, "y": 1630 },
          { "id": "tell_no", "type": "notify", "label": "Tell the employee: not approved", "x": 580, "y": 1040,
            "config": { "to": "{{submitter.email}}", "subject": "Travel not approved: {{event_title}}",
              "message": "The Board of Education did not approve this travel request, so the travel is not approved. Please do not make travel arrangements.\n{{office_notes}}" } },
          { "id": "declined", "type": "end", "label": "Travel not approved", "x": 580, "y": 1190, "config": { "outcome": "rejected" } }
        ],
        "edges": [
          { "from": "start", "port": "next", "to": "program" },
          { "from": "program", "port": "approved", "to": "central" },
          { "from": "program", "port": "rejected", "to": "declined" },
          { "from": "central", "port": "approved", "to": "business" },
          { "from": "central", "port": "rejected", "to": "declined" },
          { "from": "business", "port": "approved", "to": "superintendent" },
          { "from": "business", "port": "rejected", "to": "declined" },
          { "from": "superintendent", "port": "approved", "to": "office" },
          { "from": "superintendent", "port": "rejected", "to": "declined" },
          { "from": "office", "port": "next", "to": "board_ok" },
          { "from": "board_ok", "port": "true", "to": "tell" },
          { "from": "board_ok", "port": "false", "to": "tell_no" },
          { "from": "tell", "port": "next", "to": "after_trip" },
          { "from": "after_trip", "port": "next", "to": "reconcile" },
          { "from": "reconcile", "port": "next", "to": "tell_done" },
          { "from": "tell_done", "port": "next", "to": "done" },
          { "from": "tell_no", "port": "next", "to": "declined" }
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
          { "id": "start", "type": "start", "label": "Ticket event received", "x": 150, "y": 40 },
          { "id": "note", "type": "fs_add_note", "label": "Add escalation note", "x": 150, "y": 170,
            "config": { "body": "Urgent ticket received {{TEXT(NOW(), 'MMM d, h:mm tt')}}. Escalation workflow started.", "visibility": "private" } },
          { "id": "wait", "type": "wait_ticket", "label": "Wait until resolved", "x": 150, "y": 310, "config": { "until": "IN(ticket.status, 4, 5)" } },
          { "id": "done", "type": "end", "label": "Resolved", "x": 150, "y": 440 }
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
