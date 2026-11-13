#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Phase 0 (PLAN.md §10): fills a FRESH legacy database with synthetic data through the UNCHANGED
// legacy API, so every page has realistic content (layout matrix, characterization runs, E2E).
// Run through `tools/legacy/legacy.sh seed`, which passes LEGACY_API and LEGACY_TOKEN.
//
// Everything is fictional. Texts are deliberately long in places (client names, descriptives,
// descriptions, comments) to stress wrapping and overflow.
//
// Env: LEGACY_API (default http://localhost:3001/api), LEGACY_TOKEN (settings-capable JWT, required),
//      LEGACY_SEED_PASSWORD (password of the seeded users, default "layout-test").

using System.Net.Http.Json;
using System.Text.Json.Nodes;

var api = (Environment.GetEnvironmentVariable("LEGACY_API") ?? "http://localhost:3001/api").TrimEnd('/');
var token = Environment.GetEnvironmentVariable("LEGACY_TOKEN")
    ?? throw new InvalidOperationException("LEGACY_TOKEN is required (use legacy.sh seed)");
var password = Environment.GetEnvironmentVariable("LEGACY_SEED_PASSWORD") ?? "layout-test";

using var http = new HttpClient { BaseAddress = new Uri(api + "/") };
http.DefaultRequestHeaders.Add("x-auth-token", token);

var roles = new[] { "canSeeOrcamentos", "canSeeObras", "canSeeHistorico", "canSeeEntidades", "canSeeDefinicoes", "canSeeDashboard", "canSeeLeituras" };
var rnd = new Random(4242); // deterministic

// ---------------------------------------------------------------- users
var users = new (string Name, string Email, string[] Roles, string Field)[]
{
    ("Utilizador de Testes de Layout", "layout.tester@dib4t.local", roles, "Desenvolvimento"),
    ("Ana Rita Figueiredo de Albuquerque e Vasconcelos", "ana.vasconcelos@dib4t.local", roles[..4], "Maquinação"),
    ("João Pires", "joao.pires@dib4t.local", ["canSeeObras", "canSeeLeituras"], "Montagem"),
    ("Marta Sousa", "marta.sousa@dib4t.local", ["canSeeOrcamentos", "canSeeDashboard"], "Desenvolvimento"),
};
var userIds = new List<string>();
foreach (var u in users)
{
    var r = await Post("account/create", new { name = u.Name, email = u.Email, password, roles = u.Roles, fieldOfCompetencies = u.Field });
    userIds.Add(Id(r));
}
Console.WriteLine($"users: {userIds.Count} (password: {password})");

// ---------------------------------------------------------------- settings
foreach (var name in new[] { "Transferência bancária", "Cheque", "Numerário", "Transferência bancária a 30 dias após receção da fatura" })
    await Post("entity/addPaymentMethod", new { name });
await Post("printer-settings", new { ip = "127.0.0.1", port = 9100, templateName = "producao" });
await Post("printer-settings", new { ip = "127.0.0.2", port = 9100, templateName = "maquinacao" });
await Post("prices", new { automation = 32.5, consultancy = 45, development = 30, machining = 38, assembly = 28, margin = 35 });
Console.WriteLine("payment methods: 4, printers: 2, prices updated");

// ---------------------------------------------------------------- entities
var conditions = (await Get("entity/getAllPaymentConditions")).AsArray().Select(n => Str(n?["name"])).ToArray();
string[] clientNames =
[
    "Metalúrgica do Vale do Ave, Lda.", "Plásticos Injetados Silva & Filhos, S.A.", "Automação Industrial Norte",
    "Fábrica de Moldes e Ferramentas de Precisão da Marinha Grande — Unidade 2", "Cortiças Reunidas", "TecnoVidro",
    "Sociedade Portuguesa de Equipamentos para a Indústria Alimentar e de Bebidas, S.A.", "Rolhas & Cª",
    "Componentes Eletrónicos Ibéricos", "Têxteis Guimarães", "Embalagens Atlântico", "Robótica Lusa",
    "Mobiliário Paços de Ferreira", "Calçado Felgueiras", "Química Fina do Barreiro", "Agro-Máquinas Ribatejo",
];
string[] supplierNames = ["Aços Especiais Lda.", "Rolamentos e Transmissões", "Pneumática Central", "Parafusaria do Norte", "Eletro-Quadros", "Fornecedor de Perfis de Alumínio Extrudido e Acessórios para Máquinas"];
var clientIds = new List<string>();
var i = 0;
foreach (var n in clientNames.Concat(supplierNames))
{
    var type = i < clientNames.Length ? (i % 5 == 4 ? "Cliente e Fornecedor" : "Cliente") : "Fornecedor";
    var r = await Post("entity", new
    {
        name = n,
        fiscalNumber = (500000000 + i * 7919).ToString(),
        address = i % 3 == 0 ? "Rua da Indústria Metalomecânica e das Artes Gráficas, Lote 12, Armazém 3B, Zona Industrial" : "Av. da Liberdade, 100",
        zipCode = $"4{i:000}-{100 + i}",
        location = i % 4 == 0 ? "Vila Nova de Famalicão" : "Porto",
        paymentCondition = conditions[i % conditions.Length],
        type,
    });
    if (type != "Fornecedor") clientIds.Add(Id(r));
    i++;
}
Console.WriteLine($"entities: {clientNames.Length + supplierNames.Length} ({clientIds.Count} clients)");

// ---------------------------------------------------------------- budgets + tasks
string[] descriptives =
[
    "Máquina de corte", "Linha de montagem automática de componentes para a indústria automóvel com controlo de visão artificial",
    "Retrofit prensa 200t", "Dispositivo de controlo", "Célula robotizada de paletização", "Molde de injeção 4 cavidades",
    "Alimentador vibratório", "Bancada de testes pneumáticos", "Transportador de rolos", "Quadro elétrico e programação PLC da linha 3",
    "Garra de manipulação", "Estrutura em perfil de alumínio", "Sistema de etiquetagem", "Prensa de rolhas",
];
string[] taskTypes = ["development", "machining", "assembly", "automation", "components", "consultancy", "others"];
const string longDescription =
    "Fornecimento de equipamento conforme especificação técnica do cliente. Inclui projeto mecânico e elétrico, " +
    "fabrico de peças maquinadas em aço C45 e alumínio 6082, montagem, programação do autómato, ensaios na DIB4T " +
    "e acompanhamento do arranque nas instalações do cliente. Não inclui transporte nem trabalhos de construção civil. " +
    "Caminho de referência: \\\\DIB4T\\Orçamentos\\2026\\Cliente\\Projeto-com-um-nome-muito-comprido-sem-espacos-para-testar-quebra-de-linha";
var budgets = new List<(string Id, string Number)>();
for (var b = 0; b < descriptives.Length; b++)
{
    var budget = await Post("budget", new
    {
        clientId = clientIds[b % clientIds.Count],
        madeById = userIds[b % 2],
        descriptive = descriptives[b],
        personInCharge = b % 3 == 0 ? "Eng.ª Maria do Carmo Teixeira Lopes" : "Sr. Paulo",
    });
    var id = Id(budget);
    budgets.Add((id, Str(budget?["budgetNumber"])));

    // The default task exists already; edit it and add more (POST /api/task is broken in legacy, D8).
    var nTasks = 1 + rnd.Next(1, 6);
    var tasks = (await Get($"task/getBudgetTasks/{id}")).AsArray();
    var taskId = Id(tasks[0]!);
    for (var t = 0; t < nTasks; t++)
    {
        if (t > 0) taskId = Id(await Post($"task/createDefaultTask/{id}", new { }));
        var type = taskTypes[(b + t) % taskTypes.Length];
        await Put($"task/{taskId}/{id}", new
        {
            taskType = type,
            description = t == 1 ? "Maquinação de peças em aço inox com tolerâncias apertadas e acabamento retificado" : $"Tarefa {t + 1}",
            unitPrice = type is "components" or "others" ? 150 + rnd.Next(0, 3000) : 30 + rnd.Next(0, 15),
            quantity = rnd.Next(1, 120),
        });
    }
    await Patch($"budget/patchProperty/{id}", "description", b % 2 == 0 ? longDescription : "Conforme proposta.");
    await Patch($"budget/patchProperty/{id}", "daysNeededToDoTheWork", 5 + b * 4);
    await Patch($"budget/patchProperty/{id}", "validity", 30);
    await Patch($"budget/patchProperty/{id}", "total", 1500 + rnd.Next(0, 250000));
    if (b % 4 != 3) await Patch($"budget/patchProperty/{id}", "complete", true);
}
// Revisions for a few budgets (revision list + collapse in the budget table).
foreach (var b in new[] { 1, 4, 4, 9 })
    await Post($"budget/newrevision/{budgets[b].Id}", new { });
Console.WriteLine($"budgets: {budgets.Count} (+4 revisions)");

// ---------------------------------------------------------------- jobs (one or more per state)
string[] states = ["Desenvolvimento", "Em Aprovação", "Aprovado", "Maquinação", "Montagem", "Para faturação"];
string[] priorities = ["Alta", "Média", "Baixa"];
var today = DateTime.UtcNow.Date;
int[] dueOffsets = [-5, 2, 10, 40, -1, 6, 25, 90, 3, 15];
var jobIds = new List<string>();
for (var j = 0; j < 10; j++)
{
    var (bid, _) = budgets[j];
    var job = await Post("job", new
    {
        budgetId = bid,
        finalPrice = 2500 + rnd.Next(0, 90000),
        dueDate = today.AddDays(dueOffsets[j]).ToString("o"),
        state = states[j % states.Length],
        orderIdentification = j % 3 == 0 ? "ENC-2026/000123-REV-B-LINHA-3" : $"PO {4500 + j}",
        isOfficialOrderIdentification = j % 2 == 0,
    });
    var jid = Id(job);
    jobIds.Add(jid);
    await PatchRaw($"job/changeJobPriority/{jid}", new { priority = priorities[j % 3] });
    if (j % 3 == 1)
        await PatchRaw($"job/singleProperty/{jid}", new
        {
            propertyName = "comments",
            value = "Cliente pediu alteração das cotas da peça 12. Aguardar confirmação por email antes de avançar com a maquinação. " +
                    "Material a encomendar ao fornecedor habitual com prazo de 3 semanas.",
        });
}
// One job billed: "Para faturação" → "Faturada" through the statistics endpoint (commentType is
// required in practice: without it legacy answers 500, the UI always sends it).
var forInvoicing = jobIds[5];
var billed = await Send(HttpMethod.Put, $"job/statistics/{forInvoicing}", new
{
    orderIdentification = "ENC-2026/0099", isOfficialOrderIdentification = true, invoicedPercent = 100, state = "Faturada", commentType = Array.Empty<string>(), comments = "",
}, allowFail: true);
Console.WriteLine($"jobs: {jobIds.Count}" + (billed is null ? " (billing transition failed, see above)" : " (1 billed)"));
Console.WriteLine("seed done");

// ---------------------------------------------------------------- helpers
static string Str(JsonNode? n) => n?.GetValue<string>() ?? "";
static string Id(JsonNode? n) => Str(n?["_id"]);

async Task<JsonNode> Get(string path)
{
    var r = await http.GetAsync(path);
    return await Read(r, "GET " + path) ?? throw new InvalidOperationException($"GET {path} failed");
}
Task<JsonNode?> Post(string path, object body) => Send(HttpMethod.Post, path, body);
Task<JsonNode?> Put(string path, object body) => Send(HttpMethod.Put, path, body);
Task<JsonNode?> PatchRaw(string path, object body) => Send(HttpMethod.Patch, path, body);
Task<JsonNode?> Patch(string path, string propertyName, object value) => Send(HttpMethod.Put, path, new { propertyName, value });

async Task<JsonNode?> Send(HttpMethod method, string path, object body, bool allowFail = false)
{
    var r = await http.SendAsync(new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) });
    var node = await Read(r, $"{method} {path}", allowFail);
    return node;
}

static async Task<JsonNode?> Read(HttpResponseMessage r, string what, bool allowFail = false)
{
    var text = await r.Content.ReadAsStringAsync();
    if (!r.IsSuccessStatusCode)
    {
        var msg = $"{what} -> {(int)r.StatusCode}: {text}";
        if (allowFail) { Console.Error.WriteLine("WARN " + msg); return null; }
        throw new InvalidOperationException(msg);
    }
    try { return JsonNode.Parse(text); } catch { return JsonValue.Create(text); }
}
