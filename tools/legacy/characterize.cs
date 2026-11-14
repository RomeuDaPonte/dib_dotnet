#:package MongoDB.Bson@3.13.0
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Phase 0 (PLAN.md §10, §11.2): records CHARACTERIZATION golden files from the UNCHANGED legacy API.
// Run through `tools/legacy/legacy.sh characterize` on a FRESH legacy DB (legacy.sh up --fresh).
//
// A scripted scenario calls every endpoint of PLAN.md §6 (happy paths, validation errors, legacy
// defects, auth failures) and builds its own data as it goes. For every step it records:
//   - the request (method, path, auth label, JSON body),
//   - the response (status, content type, JSON body or text),
//   - the resulting DB changes per collection (inserted / updated / deleted documents, as canonical
//     Extended JSON, so BSON types such as Int32 vs Double are kept).
//
// The legacy server is MongoDB 4.1.4 (wire version 8) and MongoDB.Driver 3.x needs 4.4+, so the DB
// is read through the legacy app's own backup endpoint (GET /api/database/backup): raw BSON, exact
// types, no side effects on the data.
//
// Output is NORMALIZED so a re-run gives identical files (see tests/fixtures/legacy/characterization/README.md):
// ObjectIds → {{collection:n}} / {{oid:n}}, run-time dates → {{now}}, the current year → {{yyyy}}/{{yy}},
// JWTs → {{jwt}} (+ decoded payload), bcrypt hashes → {{bcrypt}}, PDF/backup bodies → summaries.
//
// SAFETY: the legacy back-end runs with NODE_ENV unset (no NAS, no printing). The one dev-mode path that
// still reaches for the NAS (PUT /api/budget/:id changing the descriptive of a revision-0 budget, defect
// D5) is never called with that input.
//
// Env: LEGACY_API (default http://localhost:3001/api), LEGACY_TOKEN (settings-capable JWT, required),
//      CHARACTERIZATION_OUT (default tests/fixtures/legacy/characterization).

using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var api = (Environment.GetEnvironmentVariable("LEGACY_API") ?? "http://localhost:3001/api").TrimEnd('/');
var toolToken = Environment.GetEnvironmentVariable("LEGACY_TOKEN")
    ?? throw new InvalidOperationException("LEGACY_TOKEN is required (use legacy.sh characterize)");
var outDir = Environment.GetEnvironmentVariable("CHARACTERIZATION_OUT") ?? "tests/fixtures/legacy/characterization";

var rec = new Recorder(api, toolToken);
await rec.Init();
var s = new Scenario(rec);
await s.Run();
rec.Write(outDir);
Console.WriteLine($"characterization: {rec.StepCount} steps written to {outDir}");
return 0;

// ===================================================================== scenario
sealed class Scenario(Recorder r)
{
    const string Pwd = "char-test-1";
    static readonly string[] AllRoles = ["canSeeOrcamentos", "canSeeObras", "canSeeHistorico", "canSeeEntidades", "canSeeDefinicoes", "canSeeDashboard", "canSeeLeituras"];
    const string Missing = "0123456789abcdef01234567"; // valid ObjectId that never exists

    public async Task Run()
    {
        // ------------------------------------------------------------ 01 auth + account
        r.Area = "01-account";
        await r.Step("auth-no-token", "Protected route without token → 401", "GET", "prices", "none");
        await r.Step("auth-invalid-token", "Protected route with a malformed token → 400", "GET", "prices", "invalid");
        await r.Step("login-validation", "Login without password → Joi message", "POST", "account/login", "none", new { email = "ana.admin@dib4t.local" });
        await r.Step("login-unknown-email", "Login with unknown email", "POST", "account/login", "none", new { email = "nobody@dib4t.local", password = Pwd });
        var ana = await r.Step("create-admin-user", "Create a user with every role", "POST", "account/create", "tool",
            new { name = "Ana Administradora", email = "ana.admin@dib4t.local", password = Pwd, passwordConfirmation = Pwd, roles = AllRoles, fieldOfCompetencies = "Desenvolvimento" });
        var bruno = await r.Step("create-limited-user", "Create a user without settings role", "POST", "account/create", "tool",
            new { name = "Bruno Maquinação", email = "bruno.limitado@dib4t.local", password = Pwd, roles = new[] { "canSeeObras", "canSeeLeituras" }, fieldOfCompetencies = "Maquinação" });
        await r.Step("create-user-default-field", "Create a user without fieldOfCompetencies (schema default)", "POST", "account/create", "tool",
            new { name = "Carla Sem Área", email = "carla.semarea@dib4t.local", password = Pwd, roles = new[] { "canSeeDashboard" } });
        await r.Step("create-user-duplicate-email", "Create a user with an existing email", "POST", "account/create", "tool",
            new { name = "Ana Duplicada", email = "ana.admin@dib4t.local", password = Pwd, roles = AllRoles });
        await r.Step("create-user-validation", "Create a user with a 2-char name", "POST", "account/create", "tool",
            new { name = "Al", email = "al.short@dib4t.local", password = Pwd, roles = AllRoles });
        await r.Step("login-wrong-password", "Login with the wrong password", "POST", "account/login", "none", new { email = "ana.admin@dib4t.local", password = "wrong-pass" });
        r.Tokens["admin"] = (await r.Step("login-admin", "Login → raw JWT string", "POST", "account/login", "none", new { email = "ana.admin@dib4t.local", password = Pwd }))!.GetValue<string>();
        r.Tokens["limited"] = (await r.Step("login-limited", "Login of the limited user", "POST", "account/login", "none", new { email = "bruno.limitado@dib4t.local", password = Pwd }))!.GetValue<string>();
        await r.Step("roles", "Role list", "GET", "account/roles", "admin");
        await r.Step("roles-forbidden", "Settings route without canSeeDefinicoes → 401", "GET", "account/roles", "limited");
        await r.Step("users", "User list sorted by name", "GET", "account/users", "admin");
        await r.Step("user-by-id", "One user", "GET", $"account/{Id(bruno)}", "admin");
        await r.Step("user-by-id-missing", "Unknown user → 404", "GET", $"account/{Missing}", "admin");
        await r.Step("user-me-legacy", "GET /account/me does not exist in legacy (cast error on /:id)", "GET", "account/me", "admin");
        await r.Step("user-update", "Update user without password", "PUT", $"account/{Id(bruno)}", "admin",
            new { name = "Bruno Maquinação Silva", email = "bruno.limitado@dib4t.local", roles = new[] { "canSeeObras", "canSeeLeituras", "canSeeHistorico" }, fieldOfCompetencies = "Montagem" });
        await r.Step("user-update-password", "Update user with a new password (re-hashed)", "PUT", $"account/{Id(bruno)}", "admin",
            new { name = "Bruno Maquinação Silva", email = "bruno.limitado@dib4t.local", roles = new[] { "canSeeObras", "canSeeLeituras", "canSeeHistorico" }, fieldOfCompetencies = "Montagem", password = "char-test-2" });
        await r.Step("user-update-missing", "Update unknown user → 404", "PUT", $"account/{Missing}", "admin",
            new { name = "Ninguém", email = "ninguem@dib4t.local", roles = AllRoles });
        await r.Step("user-update-validation", "Update user with invalid email", "PUT", $"account/{Id(bruno)}", "admin",
            new { name = "Bruno", email = "not-an-email", roles = AllRoles });
        await r.Step("login-after-password-change", "Login with the new password", "POST", "account/login", "none", new { email = "bruno.limitado@dib4t.local", password = "char-test-2" });

        // ------------------------------------------------------------ 02 settings
        r.Area = "02-settings";
        await r.Step("prices-get", "Prices (fields only, no _id)", "GET", "prices", "admin");
        await r.Step("prices-get-limited", "Prices are readable without settings role", "GET", "prices", "limited");
        await r.Step("prices-update", "Update prices", "POST", "prices", "admin", new { automation = 32.5, consultancy = 45, development = 30, machining = 38, assembly = 28, margin = 35 });
        await r.Step("prices-update-validation", "Margin 0 → 400", "POST", "prices", "admin", new { automation = 1, consultancy = 1, development = 1, machining = 1, assembly = 1, margin = 0 });
        await r.Step("prices-update-forbidden", "Prices update without settings role → 401", "POST", "prices", "limited", new { automation = 1, consultancy = 1, development = 1, machining = 1, assembly = 1, margin = 10 });
        await r.Step("payment-conditions-empty", "Payment methods on a fresh DB", "GET", "entity/getAllPaymentConditions", "admin");
        await r.Step("payment-method-add", "Add payment method", "POST", "entity/addPaymentMethod", "admin", new { name = "Pronto pagamento" });
        await r.Step("payment-method-add-2", "Add payment method with accents", "POST", "entity/addPaymentMethod", "admin", new { name = "Transferência a 30 dias" });
        var pmTemp = await r.Step("payment-method-add-3", "Add payment method (deleted below)", "POST", "entity/addPaymentMethod", "admin", new { name = "Cheque" });
        await r.Step("payment-method-duplicate", "Duplicate payment method", "POST", "entity/addPaymentMethod", "admin", new { name = "Cheque" });
        await r.Step("payment-method-validation", "Payment method without name", "POST", "entity/addPaymentMethod", "admin", new { });
        await r.Step("payment-method-delete", "Delete payment method (returns it)", "DELETE", $"entity/deletePaymentMethod/{Id(pmTemp)}", "admin");
        await r.Step("payment-method-delete-missing", "Delete unknown payment method", "DELETE", $"entity/deletePaymentMethod/{Missing}", "admin");
        await r.Step("payment-conditions", "Payment methods list", "GET", "entity/getAllPaymentConditions", "admin");
        await r.Step("printers-empty", "Printer settings on a fresh DB", "GET", "printer-settings", "admin");
        var p1 = await r.Step("printer-add-producao", "Add printer (producao)", "POST", "printer-settings", "admin", new { ip = "127.0.0.1", port = 9100, templateName = "producao" });
        var p2 = await r.Step("printer-add-maquinacao", "Add printer (maquinacao)", "POST", "printer-settings", "admin", new { ip = "127.0.0.2", port = 9100, templateName = "maquinacao" });
        await r.Step("printer-add-validation-ip", "Invalid IP", "POST", "printer-settings", "admin", new { ip = "999.1.1.1", port = 9100, templateName = "producao" });
        await r.Step("printer-add-validation-template", "Unknown template", "POST", "printer-settings", "admin", new { ip = "127.0.0.3", port = 9100, templateName = "outro" });
        await r.Step("printer-add-forbidden", "Printer route without settings role → 401", "POST", "printer-settings", "limited", new { ip = "127.0.0.3", port = 9100, templateName = "producao" });
        await r.Step("printers", "Printer list", "GET", "printer-settings", "admin");
        await r.Step("printer-by-template", "Printers by template", "GET", "printer-settings/producao", "admin");
        await r.Step("printer-by-template-missing", "Unknown template → 404", "GET", "printer-settings/outro", "admin");
        await r.Step("printer-update", "Update printer", "PUT", $"printer-settings/{Id(p1)}", "admin", new { ip = "127.0.0.11", port = 9101, templateName = "producao" });
        await r.Step("printer-update-bad-id", "Update with a non-ObjectId id", "PUT", "printer-settings/abc", "admin", new { ip = "127.0.0.11", port = 9101, templateName = "producao" });
        await r.Step("printer-update-missing", "Update unknown printer → 404", "PUT", $"printer-settings/{Missing}", "admin", new { ip = "127.0.0.11", port = 9101, templateName = "producao" });
        await r.Step("printer-delete", "Delete printer (returns it)", "DELETE", $"printer-settings/{Id(p2)}", "admin");
        await r.Step("printer-delete-missing", "Delete unknown printer → 404", "DELETE", $"printer-settings/{Missing}", "admin");
        await r.Step("printer-add-maquinacao-again", "Add printer (maquinacao) again", "POST", "printer-settings", "admin", new { ip = "127.0.0.2", port = 9100, templateName = "maquinacao" });

        // ------------------------------------------------------------ 03 entities
        r.Area = "03-entities";
        await r.Step("entity-types", "Entity types", "GET", "entity/getentityTypes", "admin");
        var c1 = await r.Step("entity-create-client", "Create client", "POST", "entity", "admin",
            new { name = "Metalúrgica do Vale do Ave, Lda.", fiscalNumber = "500100200", address = "Rua da Indústria, 12", zipCode = "4760-100", location = "Vila Nova de Famalicão", paymentCondition = "Pronto pagamento", type = "Cliente" });
        var c2 = await r.Step("entity-create-client-supplier", "Create client and supplier (only required fields)", "POST", "entity", "admin",
            new { name = "Cortiças Reunidas", paymentCondition = "Transferência a 30 dias", type = "Cliente e Fornecedor" });
        var sup = await r.Step("entity-create-supplier", "Create supplier", "POST", "entity", "admin",
            new { name = "Aços Especiais Lda.", fiscalNumber = "500300400", address = "Av. Central, 1", zipCode = "4000-001", location = "Porto", paymentCondition = "Pronto pagamento", type = "Fornecedor" });
        await r.Step("entity-create-duplicate", "Create entity with an existing name", "POST", "entity", "admin",
            new { name = "Cortiças Reunidas", paymentCondition = "Pronto pagamento", type = "Cliente" });
        await r.Step("entity-create-validation", "Create entity with unknown type", "POST", "entity", "admin",
            new { name = "Tipo Errado", paymentCondition = "Pronto pagamento", type = "Parceiro" });
        await r.Step("entity-all", "All entities sorted by name", "GET", "entity/getAll", "admin");
        await r.Step("entity-all-case-insensitive", "Routes are case-insensitive (Express)", "GET", "Entity/GETALL", "admin");
        await r.Step("entity-clients", "Clients (Cliente + Cliente e Fornecedor) sorted by name", "GET", "entity/getallclients", "admin");
        await r.Step("entity-by-id", "One entity", "GET", $"entity/{Id(c1)}", "admin");
        await r.Step("entity-by-id-missing", "Unknown entity", "GET", $"entity/{Missing}", "admin");
        await r.Step("entity-update", "Update entity (picked fields)", "PUT", $"entity/{Id(c2)}", "admin",
            new { name = "Cortiças Reunidas", fiscalNumber = "500999888", address = "Lugar da Cortiça", zipCode = "4535-000", location = "Santa Maria da Feira", paymentCondition = "Transferência a 30 dias", type = "Cliente e Fornecedor" });
        await r.Step("entity-update-missing", "Update unknown entity", "PUT", $"entity/{Missing}", "admin",
            new { name = "X", paymentCondition = "Pronto pagamento", type = "Cliente" });
        await r.Step("entity-search", "Search as the UI sends it (prefix, case-insensitive)", "POST", "entity/search", "admin",
            new { name = "meta", nif = "", localidade = "", morada = "", codigoPostal = "" });
        await r.Step("entity-search-partial-body", "Search with only name (other fields become ^undefined)", "POST", "entity/search", "admin", new { name = "a" });
        await r.Step("entity-search-no-match", "Search without match", "POST", "entity/search", "admin",
            new { name = "zzz", nif = "", localidade = "", morada = "", codigoPostal = "" });

        // ------------------------------------------------------------ 04 budgets + tasks
        r.Area = "04-budgets";
        var b1 = await r.Step("budget-create", "Create budget (number, total from prices, default task)", "POST", "budget", "admin",
            new { clientId = Id(c1), madeById = Id(ana), descriptive = "Máquina de corte", personInCharge = "Eng.ª Maria" });
        var b2 = await r.Step("budget-create-2", "Create second budget", "POST", "budget", "admin",
            new { clientId = Id(c2), madeById = Id(ana), descriptive = "Retrofit prensa 200t", personInCharge = "Sr. Paulo" });
        await r.Step("budget-create-supplier", "Create budget for a supplier → 200 text", "POST", "budget", "admin",
            new { clientId = Id(sup), madeById = Id(ana), descriptive = "Não deve existir", personInCharge = "X" });
        await r.Step("budget-create-missing-client", "Create budget for unknown client", "POST", "budget", "admin",
            new { clientId = Missing, madeById = Id(ana), descriptive = "X", personInCharge = "X" });
        await r.Step("budget-create-missing-user", "Create budget with unknown madeBy", "POST", "budget", "admin",
            new { clientId = Id(c1), madeById = Missing, descriptive = "X", personInCharge = "X" });
        await r.Step("budget-create-validation", "Create budget without descriptive", "POST", "budget", "admin",
            new { clientId = Id(c1), madeById = Id(ana), personInCharge = "X" });
        await r.Step("budget-by-id", "Budget with populated madeBy and client", "GET", $"budget/getById/{Id(b1)}", "admin");
        await r.Step("budget-by-id-missing", "Unknown budget", "GET", $"budget/getById/{Missing}", "admin");

        await r.Step("task-types", "Task types", "GET", "task/getTaskTypes", "admin");
        var b1Tasks = await r.Step("task-list", "Budget tasks (default automation task)", "GET", $"task/getBudgetTasks/{Id(b1)}", "admin");
        await r.Step("task-list-missing-budget", "Tasks of unknown budget", "GET", $"task/getBudgetTasks/{Missing}", "admin");
        var t2 = await r.Step("task-create-default", "Add default task", "POST", $"task/createDefaultTask/{Id(b1)}", "admin");
        var t3 = await r.Step("task-create-default-2", "Add default task", "POST", $"task/createDefaultTask/{Id(b1)}", "admin");
        var t4 = await r.Step("task-create-default-3", "Add default task", "POST", $"task/createDefaultTask/{Id(b1)}", "admin");
        await r.Step("task-create-default-missing", "Default task for unknown budget", "POST", $"task/createDefaultTask/{Missing}", "admin");
        await r.Step("task-update-development", "Edit task (development, integer values)", "PUT", $"task/{Id(t2)}/{Id(b1)}", "admin",
            new { taskType = "development", description = "Projeto mecânico", unitPrice = 30, quantity = 20 });
        await r.Step("task-update-machining", "Edit task (machining, decimal price)", "PUT", $"task/{Id(t3)}/{Id(b1)}", "admin",
            new { taskType = "machining", description = "Maquinação de peças em aço inox", unitPrice = 38.5, quantity = 15 });
        await r.Step("task-update-assembly", "Edit task (assembly, empty description)", "PUT", $"task/{Id(t4)}/{Id(b1)}", "admin",
            new { taskType = "assembly", description = "", unitPrice = 28, quantity = 10 });
        await r.Step("task-update-components", "Edit default task (components, decimal quantity)", "PUT", $"task/{Id(b1Tasks![0]!)}/{Id(b1)}", "admin",
            new { taskType = "components", description = "Componentes pneumáticos", unitPrice = 1250.75, quantity = 1.5 });
        await r.Step("task-update-validation", "Edit task with unknown type", "PUT", $"task/{Id(t4)}/{Id(b1)}", "admin",
            new { taskType = "painting", description = "", unitPrice = 1, quantity = 1 });
        await r.Step("task-update-bad-ids", "Edit task with non-ObjectId ids", "PUT", "task/abc/def", "admin",
            new { taskType = "assembly", description = "", unitPrice = 1, quantity = 1 });
        await r.Step("task-update-missing", "Edit unknown task", "PUT", $"task/{Missing}/{Id(b1)}", "admin",
            new { taskType = "assembly", description = "", unitPrice = 1, quantity = 1 });
        await r.Step("task-create-d8", "POST /api/task always fails validation (D8)", "POST", "task", "admin",
            new { budgetId = Id(b1), taskType = "others", description = "x", unitPrice = 1, quantity = 1 });
        var t5 = await r.Step("task-create-default-4", "Add default task (deleted below)", "POST", $"task/createDefaultTask/{Id(b1)}", "admin");
        await r.Step("task-delete", "Delete task (returns it, budget incomplete)", "DELETE", $"task/{Id(t5)}/{Id(b1)}", "admin");
        await r.Step("task-delete-missing", "Delete unknown task → 500 text", "DELETE", $"task/{Missing}/{Id(b1)}", "admin");
        await r.Step("task-list-after-edits", "Budget tasks after edits", "GET", $"task/getBudgetTasks/{Id(b1)}", "admin");

        await r.Step("budget-patch-description", "Patch description", "PUT", $"budget/patchProperty/{Id(b1)}", "admin", new { propertyName = "description", value = "Fornecimento conforme especificação.\nInclui montagem e ensaios." });
        await r.Step("budget-patch-days", "Patch daysNeededToDoTheWork", "PUT", $"budget/patchProperty/{Id(b1)}", "admin", new { propertyName = "daysNeededToDoTheWork", value = 45 });
        await r.Step("budget-patch-validity", "Patch validity", "PUT", $"budget/patchProperty/{Id(b1)}", "admin", new { propertyName = "validity", value = 60 });
        await r.Step("budget-patch-total", "Patch total (decimal)", "PUT", $"budget/patchProperty/{Id(b1)}", "admin", new { propertyName = "total", value = 12345.67 });
        await r.Step("budget-patch-margin", "Patch margin", "PUT", $"budget/patchProperty/{Id(b1)}", "admin", new { propertyName = "margin", value = 25 });
        await r.Step("budget-patch-payment", "Patch paymentMethod", "PUT", $"budget/patchProperty/{Id(b1)}", "admin", new { propertyName = "paymentMethod", value = "Transferência a 30 dias" });
        await r.Step("budget-patch-total-string", "Patch total with a numeric string (Joi converts, Mongoose casts)", "PUT", $"budget/patchProperty/{Id(b2)}", "admin", new { propertyName = "total", value = "999" });
        await r.Step("budget-patch-complete", "Patch complete=true (sets it after resetting flags)", "PUT", $"budget/patchProperty/{Id(b1)}", "admin", new { propertyName = "complete", value = true });
        await r.Step("budget-patch-bad-property", "Patch a non-whitelisted property", "PUT", $"budget/patchProperty/{Id(b1)}", "admin", new { propertyName = "budgetNumber", value = "99-9999" });
        await r.Step("budget-patch-bad-value", "Patch margin 0", "PUT", $"budget/patchProperty/{Id(b1)}", "admin", new { propertyName = "margin", value = 0 });
        await r.Step("budget-patch-missing", "Patch unknown budget", "PUT", $"budget/patchProperty/{Missing}", "admin", new { propertyName = "validity", value = 10 });
        await r.Step("budget-pdf", "PDF as base64 + wasPdfDownloaded=true", "GET", $"budget/getPdf/{Id(b1)}", "admin");
        await r.Step("budget-pdf-missing", "PDF of unknown budget → 500", "GET", $"budget/getPdf/{Missing}", "admin");

        await r.Step("budget-update", "Update rev 0 with the SAME descriptive (no NAS rename)", "PUT", $"budget/{Id(b1)}", "admin",
            new { madeById = Id(bruno), descriptive = "Máquina de corte", personInCharge = "Eng.ª Maria Lopes" });
        await r.Step("budget-update-missing-user", "Update without madeById (D7: validation skipped)", "PUT", $"budget/{Id(b1)}", "admin",
            new { descriptive = "Máquina de corte", personInCharge = "X" });
        await r.Step("budget-update-missing", "Update unknown budget", "PUT", $"budget/{Missing}", "admin",
            new { madeById = Id(ana), descriptive = "X", personInCharge = "X" });
        var b1r1 = await r.Step("budget-new-revision", "New revision (copies tasks)", "POST", $"budget/newrevision/{Id(b1)}", "admin", new { });
        await r.Step("budget-new-revision-2", "Second revision from rev 0", "POST", $"budget/newrevision/{Id(b1)}", "admin", new { });
        await r.Step("budget-new-revision-missing", "New revision of unknown budget", "POST", $"budget/newrevision/{Missing}", "admin", new { });
        await r.Step("budget-update-revision-descriptive", "Change descriptive of a revision (rev > 0: no NAS rename)", "PUT", $"budget/{Id(b1r1)}", "admin",
            new { madeById = Id(ana), descriptive = "Máquina de corte (rev. 1)", personInCharge = "Eng.ª Maria" });
        await r.Step("budget-revisions", "All revisions desc with madeBy", "GET", $"budget/revisions/{Str(b1, "budgetNumber")}", "admin");
        var b3 = await r.Step("budget-clone", "Clone to a new number (copies tasks and fields)", "POST", $"budget/clone/{Id(b1)}", "admin",
            new { clientId = Id(c2), madeById = Id(ana), descriptive = "Máquina de corte — cópia", personInCharge = "Sr. Paulo" });
        await r.Step("budget-clone-missing", "Clone unknown budget", "POST", $"budget/clone/{Missing}", "admin",
            new { clientId = Id(c2), madeById = Id(ana), descriptive = "X", personInCharge = "X" });
        await r.Step("budget-import", "Import tasks + description from a number", "PUT", $"budget/import/{Id(b2)}", "admin",
            new { importDescription = true, importTasks = true, budgetNumber = Str(b1, "budgetNumber") });
        await r.Step("budget-import-description-only", "Import description only", "PUT", $"budget/import/{Id(b3)}", "admin",
            new { importDescription = true, importTasks = false, budgetNumber = Str(b2, "budgetNumber") });
        await r.Step("budget-import-unknown-number", "Import from unknown number", "PUT", $"budget/import/{Id(b2)}", "admin",
            new { importDescription = true, importTasks = true, budgetNumber = "00-0000" });
        await r.Step("budget-import-validation", "Import without flags", "PUT", $"budget/import/{Id(b2)}", "admin",
            new { budgetNumber = Str(b1, "budgetNumber") });
        await r.Step("budget-list", "Latest revision per number, flattened rows", "GET", "budget", "admin");
        await r.Step("budget-search", "Search (regex over number/client/descriptive/person/madeBy)", "GET", "budget/search?q=corti", "admin");
        await r.Step("budget-search-number", "Search by number fragment", "GET", $"budget/search?q={Uri.EscapeDataString(Str(b1, "budgetNumber"))}", "admin");
        await r.Step("budget-search-short", "Search with 1 char → 400", "GET", "budget/search?q=a", "admin");
        await r.Step("budget-search-bad-regex", "Search with invalid regex → 500", "GET", "budget/search?q=%28%28", "admin");

        // ------------------------------------------------------------ 05 jobs
        r.Area = "05-jobs";
        // budget b4/b5 for extra jobs (clone of b1 → has development/machining/assembly tasks)
        var b4 = await r.Step("budget-for-job-2", "Clone a budget for job 2", "POST", $"budget/clone/{Id(b1)}", "admin",
            new { clientId = Id(c1), madeById = Id(ana), descriptive = "Linha de montagem", personInCharge = "Sr. Paulo" });
        var b5 = await r.Step("budget-for-job-3", "Clone a budget for job 3", "POST", $"budget/clone/{Id(b1)}", "admin",
            new { clientId = Id(c2), madeById = Id(bruno), descriptive = "Garra de manipulação", personInCharge = "Eng.ª Maria" });
        var j1 = await r.Step("job-create", "Create job from rev 1 (budget hasGenerateJob, statistics from tasks)", "POST", "job", "admin",
            new { budgetId = Id(b1r1), finalPrice = 15000.5, dueDate = "2030-01-15T00:00:00.000Z", state = "Desenvolvimento", orderIdentification = "ENC-2030/001", isOfficialOrderIdentification = true });
        await r.Step("job-create-again", "Second job for the same number", "POST", "job", "admin",
            new { budgetId = Id(b1), finalPrice = 1, dueDate = "2030-01-15T00:00:00.000Z", state = "Desenvolvimento", orderIdentification = "X", isOfficialOrderIdentification = false });
        await r.Step("job-create-validation", "Create job with state Faturada", "POST", "job", "admin",
            new { budgetId = Id(b2), finalPrice = 1, dueDate = "2030-01-15T00:00:00.000Z", state = "Faturada", orderIdentification = "X", isOfficialOrderIdentification = false });
        await r.Step("job-create-missing-budget", "Create job for unknown budget", "POST", "job", "admin",
            new { budgetId = Missing, finalPrice = 1, dueDate = "2030-01-15T00:00:00.000Z", state = "Desenvolvimento", orderIdentification = "X", isOfficialOrderIdentification = false });
        var j2 = await r.Step("job-create-2", "Create job in state Aprovado", "POST", "job", "admin",
            new { budgetId = Id(b4), finalPrice = 8000, dueDate = "2030-02-10T00:00:00.000Z", state = "Aprovado", orderIdentification = "PO 4501", isOfficialOrderIdentification = false });
        var j3 = await r.Step("job-create-3", "Create job in state Montagem", "POST", "job", "admin",
            new { budgetId = Id(b5), finalPrice = 4200, dueDate = "2030-01-05T00:00:00.000Z", state = "Montagem", orderIdentification = "PO 4502", isOfficialOrderIdentification = true });

        await r.Step("blocked-task-update", "Edit task of a budget with job", "PUT", $"task/{Id(t2)}/{Id(b1r1)}", "admin",
            new { taskType = "development", description = "x", unitPrice = 1, quantity = 1 });
        await r.Step("blocked-task-default", "Default task on a budget with job", "POST", $"task/createDefaultTask/{Id(b1r1)}", "admin");
        await r.Step("blocked-task-delete", "Delete task on a budget with job", "DELETE", $"task/{Id(t2)}/{Id(b1r1)}", "admin");
        await r.Step("blocked-patch", "Patch on a budget with job → 200 no-op", "PUT", $"budget/patchProperty/{Id(b1r1)}", "admin", new { propertyName = "validity", value = 5 });
        await r.Step("blocked-update", "Update a budget with job → 200 no-op", "PUT", $"budget/{Id(b1r1)}", "admin",
            new { madeById = Id(ana), descriptive = "Outro", personInCharge = "Outro" });
        await r.Step("blocked-import", "Import into a budget with job", "PUT", $"budget/import/{Id(b1r1)}", "admin",
            new { importDescription = true, importTasks = true, budgetNumber = Str(b2, "budgetNumber") });

        await r.Step("job-priority-high", "Priority Alta", "PATCH", $"job/changeJobPriority/{Id(j2)}", "admin", new { priority = "Alta" });
        await r.Step("job-priority-low", "Priority Baixa", "PATCH", $"job/changeJobPriority/{Id(j3)}", "admin", new { priority = "Baixa" });
        await r.Step("job-priority-validation", "Unknown priority", "PATCH", $"job/changeJobPriority/{Id(j3)}", "admin", new { priority = "Urgente" });
        await r.Step("job-priority-bad-id", "Non-ObjectId job id", "PATCH", "job/changeJobPriority/abc", "admin", new { priority = "Alta" });
        await r.Step("job-priority-missing", "Unknown job", "PATCH", $"job/changeJobPriority/{Missing}", "admin", new { priority = "Alta" });

        await r.Step("job-get-by-id", "Job with budget, client and madeBy populated", "GET", $"job/getById/{Id(j1)}", "admin");
        await r.Step("job-get-by-id-missing", "getById of unknown job → empty body", "GET", $"job/getById/{Missing}", "admin");
        await r.Step("job-get", "GET /job/:jobId", "GET", $"job/{Id(j2)}", "admin");
        await r.Step("job-get-missing", "GET /job/:jobId unknown", "GET", $"job/{Missing}", "admin");
        await r.Step("job-get-bad-id", "GET /job/:jobId non-ObjectId", "GET", "job/abc", "admin");
        await r.Step("job-counts", "Counters per state", "GET", "job/getCounts", "admin");
        await r.Step("job-unfinished", "Unfinished jobs ordered by priority then due date", "GET", "job/getUnfinished", "admin");
        await r.Step("job-by-state", "Jobs in state", "GET", $"job/getByState/{Uri.EscapeDataString("Aprovado")}", "admin");
        await r.Step("job-by-state-validation", "Unknown state", "GET", "job/getByState/Outro", "admin");
        await r.Step("job-by-budget-number", "Job by budget number", "GET", $"job/getByBudgetNumber/{Str(b1, "budgetNumber")}", "admin");
        await r.Step("job-by-budget-number-missing", "No job for that number → empty body", "GET", $"job/getByBudgetNumber/{Str(b2, "budgetNumber")}", "admin");
        await r.Step("job-by-budget-id", "Job id by budget id (JSON string)", "GET", $"job/getByBudgetId/{Id(b4)}", "admin");
        await r.Step("job-by-budget-id-missing", "No job for that budget", "GET", $"job/getByBudgetId/{Id(b2)}", "admin");

        await r.Step("job-due-date", "Change due date", "PATCH", $"job/dueDate/{Id(j2)}", "admin", new { dueDate = "2030-03-01T00:00:00.000Z" });
        await r.Step("job-due-date-missing", "Change due date of unknown job", "PATCH", $"job/dueDate/{Missing}", "admin", new { dueDate = "2030-03-01T00:00:00.000Z" });
        await r.Step("job-statistics-dates-no-dates", "Set a state date on a job without jobStatisticsDates → 500", "PATCH", $"job/statisticsDates/{Id(j2)}", "admin",
            new { forJobState = "devEndedInDate", newDate = "2030-01-01T00:00:00.000Z" });
        await r.Step("job-statistics-dates-validation", "Unknown state date name", "PATCH", $"job/statisticsDates/{Id(j2)}", "admin",
            new { forJobState = "billedEndedInDate", newDate = "2030-01-01T00:00:00.000Z" });
        foreach (var (type, value) in new (string, object)[] { ("development", 12), ("consultancy", 3), ("assembly", 4.5), ("automation", 99), ("components", 50), ("machining", 7), ("others", 3), ("custoNaoQualidade", 15), ("materials", 20) })
            await r.Step($"job-register-{type}", $"Register value for {type}", "PATCH", $"job/register/{Id(j2)}", "admin", new { newRegisteredValue = value, taskType = type });
        await r.Step("job-register-missing", "Register on unknown job", "PATCH", $"job/register/{Missing}", "admin", new { newRegisteredValue = 1, taskType = "others" });
        await r.Step("job-state-d1", "PATCH /job/state always 400 (D1)", "PATCH", $"job/state/{Id(j2)}", "admin", new { state = "Apagada" });
        await r.Step("job-state-validation", "PATCH /job/state with unknown state", "PATCH", $"job/state/{Id(j2)}", "admin", new { state = "Outro" });
        await r.Step("job-single-comments", "singleProperty comments", "PATCH", $"job/singleProperty/{Id(j2)}", "admin", new { propertyName = "comments", value = "Aguardar confirmação do cliente." });
        await r.Step("job-single-order", "singleProperty orderIdentification", "PATCH", $"job/singleProperty/{Id(j2)}", "admin", new { propertyName = "orderIdentification", value = "ENC-2030/777" });
        await r.Step("job-single-official", "singleProperty isOfficialOrderIdentification", "PATCH", $"job/singleProperty/{Id(j2)}", "admin", new { propertyName = "isOfficialOrderIdentification", value = true });
        await r.Step("job-single-invoiced", "singleProperty invoicedPercent", "PATCH", $"job/singleProperty/{Id(j2)}", "admin", new { propertyName = "invoicedPercent", value = 25 });
        await r.Step("job-single-delivery-blocked", "deliveryDate not allowed in Aprovado", "PATCH", $"job/singleProperty/{Id(j2)}", "admin", new { propertyName = "deliveryDate", value = "2030-02-20T00:00:00.000Z" });
        await r.Step("job-single-delivery", "deliveryDate allowed in Montagem", "PATCH", $"job/singleProperty/{Id(j3)}", "admin", new { propertyName = "deliveryDate", value = "2030-02-20T00:00:00.000Z" });
        await r.Step("job-single-validation", "Unknown property", "PATCH", $"job/singleProperty/{Id(j2)}", "admin", new { propertyName = "finalPrice", value = 1 });

        var j2Node = await r.Get($"job/getById/{Id(j2)}");
        var ms = j2Node!["machiningStatistic"]!.DeepClone();
        ms["verificationAndShapeControl"] = "Ok"; ms["dimensionalVerificationAndControl"] = "Nok";
        await r.Step("job-verifications-machining", "Update machining verifications (UI sends both objects)", "PATCH", $"job/updateVerifications/{Id(j2)}", "admin",
            new JsonObject { ["machiningStatistic"] = ms, ["assemblyStatistic"] = j2Node["assemblyStatistic"]!.DeepClone(), ["updateMachiningVerificatons"] = true });
        var j3Node = await r.Get($"job/getById/{Id(j3)}");
        var asm = j3Node!["assemblyStatistic"]!.DeepClone();
        asm["kinematics"] = "Ok"; asm["visualInspection"] = "Nok"; asm["validTryOnFirstTest"] = "3";
        await r.Step("job-verifications-assembly", "Update assembly verifications", "PATCH", $"job/updateVerifications/{Id(j3)}", "admin",
            new JsonObject { ["machiningStatistic"] = j3Node["machiningStatistic"]!.DeepClone(), ["assemblyStatistic"] = asm, ["updateMachiningVerificatons"] = false });
        await r.Step("job-print-dev", "Print outside production → 400", "POST", $"job/print/{Id(b4)}", "admin", new { maquinacao = true, producao = true });

        // State workflow of job 1 through PUT /statistics (tasks of b1r1: development 20, machining 15, assembly 10)
        var sid = $"job/statistics/{Id(j1)}";
        object Dev(string state, int hours, int deviation, int invoiced = 0) => new
        {
            commentType = new[] { "Detalhes de fabrico" }, numberOfHoursUsed = hours, components = 120.5, automation = 40, invoicedPercent = invoiced,
            state, budgetedHours = 20, deviation, comments = "Desenvolvimento concluído.",
        };
        await r.Step("stats-validation", "Statistics without numberOfHoursUsed", "PUT", sid, "admin", new { state = "Em Aprovação", components = 0, automation = 0, budgetedHours = 20, deviation = 0 });
        await r.Step("stats-bad-deviation", "Wrong deviation → 400", "PUT", sid, "admin", Dev("Em Aprovação", 18, 5));
        await r.Step("stats-dev-to-onapproval", "Desenvolvimento → Em Aprovação", "PUT", sid, "limited", Dev("Em Aprovação", 18, 2));
        await r.Step("job-statistics-dates", "Set a state date (job now has jobStatisticsDates)", "PATCH", $"job/statisticsDates/{Id(j1)}", "admin",
            new { forJobState = "devEndedInDate", newDate = "2030-01-02T00:00:00.000Z" });
        await r.Step("stats-onapproval-to-approved", "Em Aprovação → Aprovado", "PUT", sid, "admin", Dev("Aprovado", 19, 1, 10));
        await r.Step("stats-approved-to-machining", "Aprovado → Maquinação", "PUT", sid, "admin", Dev("Maquinação", 20, 0, 20));
        await r.Step("stats-machining-to-assembly", "Maquinação → Montagem (machining statistics, validatedBy)", "PUT", sid, "limited", new
        {
            numberOfHoursUsed = 14, materials = 300, state = "Montagem", budgetedHours = 15, invoicedPercent = 30, deviation = 1,
            verificationAndShapeControl = "Ok", dimensionalVerificationAndControl = "Nok", analysisOfTheDegreeOfFinish = "n/a",
            comments = "Peça 12 retrabalhada.", commentType = new[] { "Tipo de material", "Tolerâncias a respeitar" },
        });
        await r.Step("stats-assembly-to-invoicing", "Montagem → Para faturação (assembly statistics, deliveryDate)", "PUT", sid, "admin", new
        {
            numberOfHoursUsed = 12, components = 150, commentType = Array.Empty<string>(), automation = 45, state = "Para faturação", budgetedHours = 10,
            invoicedPercent = 60, deviation = -2, pneumaticCircuits = "Ok", electricalBoardsAndCircuits = "Ok", kinematics = "Nok",
            programmingFeaturesPLC = "n/a", operator_equipmentSafetyConditions = "Ok", visualInspection = "Ok", validTryOnFirstTest = "2",
            deliveryDate = "2030-03-01T00:00:00.000Z", comments = "",
        });
        await r.Step("stats-invoicing-no-commenttype", "Para faturação without commentType → 500", "PUT", sid, "admin", new
        {
            orderIdentification = "ENC-2030/001", isOfficialOrderIdentification = true, invoicedPercent = 100, state = "Faturada", comments = "",
        });
        await r.Step("stats-invoicing-partial", "Faturada with invoicedPercent < 100 → 405 {reason, updatedJob}", "PUT", sid, "admin", new
        {
            deliveryDate = "2030-03-02T00:00:00.000Z", orderIdentification = "ENC-2030/001-A", isOfficialOrderIdentification = true, invoicedPercent = 80,
            state = "Faturada", comments = "Falta faturar 20 %.", commentType = Array.Empty<string>(),
        });
        await r.Step("stats-invoicing-to-billed", "Para faturação → Faturada (invoicedBy)", "PUT", sid, "admin", new
        {
            deliveryDate = "2030-03-02T00:00:00.000Z", orderIdentification = "ENC-2030/001-A", isOfficialOrderIdentification = true, invoicedPercent = 100,
            state = "Faturada", comments = "", commentType = Array.Empty<string>(),
        });
        await r.Step("stats-jump-dev-to-assembly", "Aprovado → Montagem skips Maquinação (updateStateDates fills the gaps)", "PUT", $"job/statistics/{Id(j2)}", "admin", new
        {
            commentType = Array.Empty<string>(), numberOfHoursUsed = 25, components = 0, automation = 0, invoicedPercent = 0,
            state = "Montagem", budgetedHours = 20, deviation = -5, comments = "",
        });
        await r.Step("stats-missing-job", "Statistics for unknown job", "PUT", $"job/statistics/{Missing}", "admin", Dev("Em Aprovação", 18, 2));
        await r.Step("job-by-state-billed", "Billed jobs (not re-ordered by priority)", "GET", $"job/getByState/{Uri.EscapeDataString("Faturada")}", "admin");
        await r.Step("job-by-state-assembly", "Jobs in Montagem (priority order)", "GET", $"job/getByState/{Uri.EscapeDataString("Montagem")}", "admin");
        await r.Step("job-all", "All jobs ordered by priority then due date", "GET", "job/getAll", "admin");
        await r.Step("job-counts-after", "Counters after the workflow", "GET", "job/getCounts", "admin");
        await r.Step("job-valid-try-counters", "Dashboard counters for the current year", "GET", $"job/getValidTryOnFirstTestCountersByYear/{DateTime.UtcNow.Year}", "admin");
        await r.Step("job-valid-try-counters-other-year", "Dashboard counters for a year without jobs", "GET", "job/getValidTryOnFirstTestCountersByYear/2001", "admin");
        await r.Step("budget-list-after-jobs", "Budget list after jobs (hasGenerateJob)", "GET", "budget", "admin");

        // ------------------------------------------------------------ 06 database
        r.Area = "06-database";
        await r.Step("db-info", "Database info (sizes masked)", "GET", "database/info", "admin");
        await r.Step("db-info-forbidden", "Database info without settings role", "GET", "database/info", "limited");
        var backup = await r.StepBackup("db-backup", "Backup download (body summarized)");
        await r.StepRaw("db-restore-empty", "Restore without a body", "POST", "database/restore", "admin", [], "application/octet-stream", "(empty)");
        await r.StepRaw("db-restore-garbage", "Restore a non-backup file", "POST", "database/restore", "admin", "this is not a backup"u8.ToArray(), "application/octet-stream", "the ASCII text 'this is not a backup'");
        await r.StepRaw("db-restore-truncated", "Restore a truncated backup", "POST", "database/restore", "admin", backup[..(backup.Length / 2)], "application/octet-stream", "first half of the db-backup body");
        await r.StepRaw("db-restore", "Restore the backup just taken (DB unchanged)", "POST", "database/restore", "admin", backup, "application/octet-stream", "the db-backup body");

        // ------------------------------------------------------------ 07 destructive legacy defects (last)
        r.Area = "07-account-delete";
        await r.Step("user-delete-d2", "DELETE /account/:id deletes the FIRST user, not :id (D2)", "DELETE", $"account/{Id(bruno)}", "admin");
        await r.Step("user-delete-missing", "Delete unknown user → 404", "DELETE", $"account/{Missing}", "admin");
        await r.Step("users-after-delete", "Users after the D2 delete", "GET", "account/users", "admin");
        await r.Step("budget-list-after-delete", "Budget list still loads (madeBy users kept)", "GET", "budget", "admin");
    }

    static string Str(JsonNode? n, string prop) => n?[prop]?.GetValue<string>() ?? throw new InvalidOperationException($"missing {prop} in {n?.ToJsonString()}");
    static string Id(JsonNode? n) => Str(n, "_id");
}

// ===================================================================== recorder
sealed class Recorder(string api, string toolToken)
{
    readonly HttpClient http = new() { BaseAddress = new Uri(api + "/"), Timeout = TimeSpan.FromSeconds(60) };
    public readonly Dictionary<string, string> Tokens = new() { ["tool"] = toolToken, ["invalid"] = "not-a-jwt" };
    public string Area = "00";
    readonly List<(string Area, JsonObject Step)> steps = [];
    readonly Normalizer norm = new();
    Dictionary<string, Dictionary<string, BsonDocument>> db = [];
    JsonObject initialDb = new();
    readonly HashSet<string> ids = [];
    public int StepCount => steps.Count;

    public async Task Init()
    {
        db = await Snapshot();
        var users = db.GetValueOrDefault("users")?.Count ?? 0;
        if (users != 1 || db.Keys.Except(["users", "prices"]).Any(k => db[k].Count > 0))
            throw new InvalidOperationException($"characterization needs a FRESH legacy DB (found {users} users); run: legacy.sh up --fresh");
        initialDb = DbJson(db);
    }

    public async Task<JsonNode?> Get(string path)
    {
        using var req = Build("GET", path, "tool", null);
        using var res = await http.SendAsync(req);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync());
    }

    public async Task<JsonNode?> Step(string id, string title, string method, string path, string auth, object? body = null)
    {
        var bodyNode = body is null ? null : body as JsonNode ?? JsonSerializer.SerializeToNode(body);
        using var req = Build(method, path, auth, bodyNode is null ? null : new StringContent(bodyNode.ToJsonString(), Encoding.UTF8, "application/json"));
        using var res = await http.SendAsync(req);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        var ct = res.Content.Headers.ContentType?.ToString() ?? "";
        var text = Encoding.UTF8.GetString(bytes);
        JsonNode? parsed = null;
        if (ct.StartsWith("application/json")) parsed = text.Length == 0 ? null : JsonNode.Parse(text);
        else if (text.Length > 0) parsed = JsonValue.Create(text);

        var response = new JsonObject { ["status"] = (int)res.StatusCode, ["contentType"] = ct };
        if (ct.StartsWith("application/pdf")) response["body"] = PdfSummary(text);
        else if (ct.StartsWith("application/json")) response["json"] = parsed?.DeepClone();
        else if (Recorder.ExpressErrorPage(text) is { } page) response["expressErrorPage"] = page;
        else
        {
            response["text"] = text;
            if (Normalizer.IsJwt(text)) response["jwtPayload"] = Normalizer.JwtPayload(text);
        }
        await Record(id, title, method, path, auth, bodyNode?.DeepClone(), response);
        return (int)res.StatusCode < 300 ? parsed : null;
    }

    public async Task StepRaw(string id, string title, string method, string path, string auth, byte[] body, string contentType, string describe)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var req = Build(method, path, auth, content);
        using var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        var ct = res.Content.Headers.ContentType?.ToString() ?? "";
        var response = new JsonObject { ["status"] = (int)res.StatusCode, ["contentType"] = ct };
        if (ct.StartsWith("application/json")) response["json"] = JsonNode.Parse(text); else response["text"] = text;
        var reqBody = new JsonObject { ["contentType"] = contentType, ["content"] = describe };
        await Record(id, title, method, path, auth, reqBody, response);
    }

    public async Task<byte[]> StepBackup(string id, string title)
    {
        using var req = Build("GET", "database/backup", "admin", null);
        using var res = await http.SendAsync(req);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        var (header, cols, end) = Dibbak.Parse(bytes);
        var response = new JsonObject
        {
            ["status"] = (int)res.StatusCode,
            ["contentType"] = res.Content.Headers.ContentType?.ToString(),
            ["contentDisposition"] = Regex.Replace(res.Content.Headers.ContentDisposition?.ToString() ?? "", @"dib4t-backup-[0-9TZ-]+\.dibbak", "dib4t-backup-{{stamp}}.dibbak"),
            ["accessControlExposeHeaders"] = res.Headers.TryGetValues("Access-Control-Expose-Headers", out var v) ? string.Join(",", v) : null,
            ["body"] = new JsonObject
            {
                ["gzip"] = bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b,
                ["magic"] = "DIBBAK01",
                ["header"] = header,
                ["collections"] = new JsonArray(cols.Select(c => (JsonNode)new JsonObject
                {
                    ["meta"] = c.Meta.DeepClone(),
                    ["documents"] = c.Docs.Count,
                }).ToArray()),
                ["end"] = end,
            },
        };
        await Record(id, title, "GET", "database/backup", "admin", null, response);
        return bytes;
    }

    async Task Record(string id, string title, string method, string path, string auth, JsonNode? body, JsonObject response)
    {
        if (!ids.Add(id)) throw new InvalidOperationException($"duplicate step id {id}");
        var after = await Snapshot();
        // Name every top-level _id by collection before normalizing anything of this step.
        foreach (var (col, docs) in after.OrderBy(k => k.Key, StringComparer.Ordinal))
            foreach (var d in docs.Values) if (d["_id"] is BsonObjectId oid) norm.Name(oid.Value.ToString(), col);

        var changes = new JsonObject();
        foreach (var col in after.Keys.Union(db.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var before = db.GetValueOrDefault(col) ?? [];
            var now = after.GetValueOrDefault(col) ?? [];
            var inserted = new JsonArray(); var updated = new JsonArray(); var deleted = new JsonArray();
            foreach (var (key, doc) in now)
            {
                if (!before.TryGetValue(key, out var old)) inserted.Add(norm.Doc(doc));
                else if (!old.Equals(doc))
                    updated.Add(new JsonObject
                    {
                        ["_id"] = norm.Doc(new BsonDocument("_id", doc["_id"]))["_id"]!.DeepClone(),
                        ["changed"] = new JsonArray(doc.Names.Union(old.Names).Where(n => !doc.Contains(n) || !old.Contains(n) || !doc[n].Equals(old[n])).Select(n => (JsonNode)n).ToArray()),
                        ["after"] = norm.Doc(doc),
                    });
            }
            foreach (var (key, doc) in before) if (!now.ContainsKey(key)) deleted.Add(norm.Doc(doc));
            if (inserted.Count + updated.Count + deleted.Count == 0) continue;
            var c = new JsonObject();
            if (inserted.Count > 0) c["inserted"] = inserted;
            if (updated.Count > 0) c["updated"] = updated;
            if (deleted.Count > 0) c["deleted"] = deleted;
            changes[col] = c;
        }
        db = after;

        if (path.StartsWith("database/info")) Mask(response["json"]);
        var step = new JsonObject
        {
            ["id"] = id,
            ["title"] = title,
            ["request"] = new JsonObject { ["method"] = method, ["path"] = "/api/" + norm.Text(path), ["auth"] = auth, ["body"] = norm.Json(body) },
            ["response"] = norm.Json(response),
            ["db"] = changes,
        };
        steps.Add((Area, step));
        Console.WriteLine($"  {Area} {id,-45} {response["status"]}");
    }

    // Unhandled errors: the legacy error middleware (middleware/error.js, "Something failed.") is never
    // registered, so Express answers with its default HTML page holding the stack trace. Keep only the
    // error line; the stack has local absolute paths and is not part of the contract.
    public static JsonObject? ExpressErrorPage(string text)
    {
        var m = Regex.Match(text, @"^<!DOCTYPE html>.*?<title>Error</title>.*?<pre>(.*?)</pre>", RegexOptions.Singleline);
        if (!m.Success) return null;
        var first = System.Net.WebUtility.HtmlDecode(Regex.Split(m.Groups[1].Value, "<br>")[0]).Trim();
        return new JsonObject { ["error"] = first };
    }

    static void Mask(JsonNode? n)
    {
        string[] varying = ["size", "avgObjSize", "storageSize", "indexSize", "dataSize"];
        if (n is JsonObject o)
            foreach (var k in o.Select(p => p.Key).ToList())
                if (varying.Contains(k) && o[k] is JsonValue) o[k] = "{{varies}}"; else Mask(o[k]);
        else if (n is JsonArray a) foreach (var x in a) Mask(x);
    }

    static JsonObject PdfSummary(string base64)
    {
        byte[] pdf;
        try { pdf = Convert.FromBase64String(base64); } catch { return new JsonObject { ["base64"] = false }; }
        var head = Encoding.ASCII.GetString(pdf, 0, Math.Min(8, pdf.Length));
        return new JsonObject { ["base64"] = true, ["decodedStartsWith"] = head, ["decodedEndsWithEof"] = Encoding.ASCII.GetString(pdf[^Math.Min(8, pdf.Length)..]).Contains("%%EOF") };
    }

    HttpRequestMessage Build(string method, string path, string auth, HttpContent? content)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
        if (auth != "none") req.Headers.TryAddWithoutValidation("x-auth-token", Tokens[auth]);
        return req;
    }

    async Task<Dictionary<string, Dictionary<string, BsonDocument>>> Snapshot()
    {
        using var req = Build("GET", "database/backup", "tool", null);
        using var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var (_, cols, _) = Dibbak.Parse(await res.Content.ReadAsByteArrayAsync());
        return cols.ToDictionary(c => c.Name, c => c.Docs.ToDictionary(d => d["_id"].ToString()!, d => d));
    }

    JsonObject DbJson(Dictionary<string, Dictionary<string, BsonDocument>> snap)
    {
        foreach (var (col, docs) in snap.OrderBy(k => k.Key, StringComparer.Ordinal))
            foreach (var d in docs.Values) if (d["_id"] is BsonObjectId oid) norm.Name(oid.Value.ToString(), col);
        var o = new JsonObject();
        foreach (var (col, docs) in snap.OrderBy(k => k.Key, StringComparer.Ordinal))
            o[col] = new JsonArray(docs.Values.Select(d => (JsonNode)norm.Doc(d)).ToArray());
        return o;
    }

    public void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "*.json")) File.Delete(f);
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        void Save(string name, JsonNode node) => File.WriteAllText(Path.Combine(dir, name), node.ToJsonString(opts) + "\n");
        Save("00-initial-db.json", initialDb);
        foreach (var g in steps.GroupBy(s => s.Area))
            Save(g.Key + ".json", new JsonObject { ["area"] = g.Key, ["steps"] = new JsonArray(g.Select(s => (JsonNode)s.Step).ToArray()) });
        Save("99-final-db.json", DbJson(db));
        var index = new JsonArray(steps.Select(s => (JsonNode)new JsonObject
        {
            ["file"] = s.Area + ".json", ["id"] = s.Step["id"]!.DeepClone(),
            ["endpoint"] = $"{s.Step["request"]!["method"]} {Normalizer.Route(s.Step["request"]!["path"]!.GetValue<string>())}",
            ["status"] = s.Step["response"]!["status"]!.DeepClone(),
        }).ToArray());
        Save("index.json", index);
    }
}

// ===================================================================== normalization
sealed class Normalizer
{
    readonly Dictionary<string, string> names = [];
    readonly Dictionary<string, int> counters = [];
    readonly DateTime start = DateTime.UtcNow.AddMinutes(-1);
    readonly string yyyy = DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture);
    static readonly Regex Hex24 = new("^[0-9a-f]{24}$");
    static readonly Regex Hex24Any = new("[0-9a-f]{24}");
    static readonly Regex Bcrypt = new(@"^\$2[aby]\$\d\d\$[./A-Za-z0-9]{53}$");
    static readonly Regex IsoDate = new(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z$");
    static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    public string Name(string hex, string prefix)
    {
        if (names.TryGetValue(hex, out var n)) return n;
        counters[prefix] = counters.GetValueOrDefault(prefix) + 1;
        return names[hex] = $"{{{{{prefix}:{counters[prefix]}}}}}";
    }

    bool IsNow(DateTime d) => d >= start && d <= DateTime.UtcNow.AddMinutes(1);

    public JsonNode Doc(BsonDocument d) => Json(JsonNode.Parse(d.ToJson(Canonical)))!;

    public JsonNode? Json(JsonNode? n)
    {
        switch (n)
        {
            case null: return null;
            case JsonObject o when o.Count == 1 && o["$oid"] is JsonValue ov:
                return new JsonObject { ["$oid"] = Name(ov.GetValue<string>(), "oid") };
            case JsonObject o when o.Count == 1 && o["$date"] is JsonObject dv:
                var ms = long.Parse(dv["$numberLong"]!.GetValue<string>(), CultureInfo.InvariantCulture);
                var date = DateTime.UnixEpoch.AddMilliseconds(ms);
                return new JsonObject { ["$date"] = IsNow(date) ? "{{now}}" : date.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) };
            case JsonObject o:
                var r = new JsonObject();
                foreach (var (k, v) in o) r[k] = Json(v);
                return r;
            case JsonArray a:
                return new JsonArray(a.Select(Json).ToArray());
            case JsonValue v when v.TryGetValue<string>(out var str):
                return JsonValue.Create(Text(str));
            default:
                return n.DeepClone();
        }
    }

    public string Text(string s)
    {
        if (Hex24.IsMatch(s)) return Name(s, "oid");
        if (IsJwt(s)) return "{{jwt}}";
        if (Bcrypt.IsMatch(s)) return "{{bcrypt:" + s[..7] + "}}";
        if (IsoDate.IsMatch(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) && IsNow(d))
            return "{{now}}";
        s = Regex.Replace(s, @"(/Users|/home|[A-Z]:\\Users)/[^\s""'<>]*", "{{local-path}}");
        s = Hex24Any.Replace(s, m => names.TryGetValue(m.Value, out var nm) ? nm : m.Value);
        s = Regex.Replace(s, $@"(?<![0-9]){yyyy[2..]}-(\d{{4}})(?![0-9])", "{{yy}}-$1");
        s = Regex.Replace(s, $@"(?<![0-9]){yyyy}(?![0-9])", "{{yyyy}}");
        return s;
    }

    public static bool IsJwt(string s) => s.StartsWith("eyJ") && s.Count(c => c == '.') == 2;

    public static JsonNode JwtPayload(string jwt)
    {
        var p = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
        var node = JsonNode.Parse(Convert.FromBase64String(p))!.AsObject();
        if (node.ContainsKey("iat")) node["iat"] = "{{iat}}";
        return node;
    }

    // "/api/job/getById/{{jobs:1}}" → "/api/job/getById/:id" for the index
    public static string Route(string path) =>
        Regex.Replace(Regex.Replace(Regex.Replace(path.Split('?')[0], @"\{\{[a-z]+:\d+\}\}", ":id"), @"/[0-9a-f]{24}", "/:id"), @"/\{\{yy\}\}-\d{4}", "/:budgetNumber");
}

// ===================================================================== .dibbak (same format as dibbak.cs)
static class Dibbak
{
    public record Col(string Name, JsonNode Meta, List<BsonDocument> Docs);

    public static (JsonNode Header, List<Col> Cols, JsonNode End) Parse(byte[] gz)
    {
        using var z = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        var buf = ms.ToArray();
        if (!buf.AsSpan(0, 8).SequenceEqual("DIBBAK01"u8)) throw new InvalidDataException("not a .dibbak");
        var off = 8;
        JsonNode? header = null, end = null;
        var cols = new List<Col>();
        while (off < buf.Length)
        {
            var type = (char)buf[off];
            var len = BitConverter.ToInt32(buf, off + 1);
            var payload = buf.AsSpan(off + 5, len).ToArray();
            off += 5 + len;
            switch (type)
            {
                case 'H': header = JsonNode.Parse(payload); break;
                case 'C': var meta = JsonNode.Parse(payload)!; cols.Add(new Col(meta["name"]!.GetValue<string>(), meta, [])); break;
                case 'D':
                    using (var reader = new BsonBinaryReader(new MemoryStream(payload)))
                        cols[^1].Docs.Add(BsonSerializer.Deserialize<BsonDocument>(reader));
                    break;
                case 'Z': end = JsonNode.Parse(payload); break;
                default: throw new InvalidDataException($"unknown frame '{type}'");
            }
        }
        return (header ?? throw new InvalidDataException("no header"), cols, end ?? throw new InvalidDataException("no end frame"));
    }
}
