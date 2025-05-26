using Microsoft.Data.SqlClient;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

internal class Program
{
	static void Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(args);

		const string SqlConnectionString = ""; //

		const string YoutrackUrl = ""; //
		const string YoutrackToken = "perm:"; //

		const int ThresholdMinutes = 7 * 60;

		// Константы для бота
		const string BotToken = ""; //
		const string ApiUrl = "https://botapi.messenger.yandex.net/";

		// Регистрируем HttpClient с настройками
		builder.Services.AddHttpClient("YandexMessengerBot", client =>
		{
			client.BaseAddress = new Uri(ApiUrl);
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("OAuth", BotToken);
		});

		var app = builder.Build();
		app.UseRouting();


		// 3. Маршрут для приёма webhook

		app.MapPost("/webhook", async (HttpRequest req, IHttpClientFactory httpFactory) =>
		{
			// Парсим JSON
			using var jsonDoc = await JsonDocument.ParseAsync(req.Body);
			var root = jsonDoc.RootElement;

			var (absent, totals) = await GetWorkReportAsync();
			var absentList = absent.ToList();
			var totalList = totals.ToList();

			// Проверяем, что это действительно массив updates
			if (root.TryGetProperty("updates", out var updates) && updates.ValueKind == JsonValueKind.Array)
			{
				foreach (var upd in updates.EnumerateArray())
				{
					// Получаем chat.type и пропускаем не-private
					if (!upd.TryGetProperty("chat", out var chat) ||
						chat.GetProperty("type").GetString() != "private")
						continue;


					// Берём информацию об отправителе
					var from = upd.GetProperty("from");
					var login = from.GetProperty("login").GetString()!;
					var text = upd.GetProperty("text").GetString()!;

					if (text == "Указать номер задачи")
					{

					}
					if (text == "Создать отсутствие на сегодня")
					{
						await CreateAbsenceForTodayAsync(login);
						await SendTextAsync(login, "Отсутствие создано на сегодня!", httpFactory);
						continue;
					}
					if (text == "Получить список задач")
					{
						var WorkedIssues = await GetTodayWorkedIssuesAsync(""); //
																				// Например, выводим первую задачу:
						var firstIssueId = WorkedIssues.Keys.First();
						var firstSummary = WorkedIssues[firstIssueId].Summary;
						var firstDuration = WorkedIssues[firstIssueId].Duration;

						await SendTextAsync(login, firstIssueId, httpFactory);
						await SendTextAsync(login, firstSummary, httpFactory);
						await SendTextAsync(login, firstDuration.ToString(), httpFactory);
					}


					// Получение ответа от пользователя
					Console.WriteLine($"[INFO] Сообщение от {login}: {text}");

					await SendTextAsync(login, $"{text} \n Тебе тоже привет пользователь", httpFactory);
				}

				return Results.Ok();
			}

			// Если нет поля "updates" — просто возвращаем 200
			return Results.NoContent();
		});



		app.Run();



		// Метод получения списка задач, над которыми сегодня работал юзер
		static async Task<Dictionary<string, (int Duration, string Summary)>> GetTodayWorkedIssuesAsync(string login)
		{
			var today = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
			using var http = new HttpClient { BaseAddress = new Uri(YoutrackUrl) };
			http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", YoutrackToken);

			// Добавляем author=<login> в URL
			var url = $"api/workItems?fields=issue(id,summary),duration(minutes)&$top=1000&startDate={today}&endDate={today}&author={login}";

			var resp = await http.GetAsync(url);
			resp.EnsureSuccessStatusCode();
			var arr = JArray.Parse(await resp.Content.ReadAsStringAsync());

			// Группируем по issue.id
			return arr
				.GroupBy(item => item["issue"]!["id"]!.ToString())
				.ToDictionary(
					g => g.Key,
					g => (
						Duration: g.Sum(x => x["duration"]?["minutes"]?.ToObject<int>() ?? 0),
						Summary: g.First()!["issue"]!["summary"]!.ToString()
					)
				);
		}

		static async Task<(HashSet<string> absentEmails, Dictionary<string, int> totalByEmail)> GetWorkReportAsync()
		{
			var today = DateTime.UtcNow.Date;

			// 1. Получаем список отсутствующих по Email
			var absent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			using (var conn = new SqlConnection(SqlConnectionString))
			{
				await conn.OpenAsync();
				var sql = @"
                    SELECT p.Email
                    FROM hr.ActualAbsence a
                    JOIN em.Employee p ON a.ID_em_Employee = p.ID
                    WHERE @today BETWEEN CAST(a.DateBegin AS date) AND CAST(a.DateEnd AS date)";
				using var cmd = new SqlCommand(sql, conn);
				cmd.Parameters.AddWithValue("@today", today);
				using var rdr = await cmd.ExecuteReaderAsync();
				while (await rdr.ReadAsync())
				{
					absent.Add(rdr.GetString(0));
				}
			}

			// 2. Получаем всех активных пользователей из YouTrack
			var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

			using var http = new HttpClient { BaseAddress = new Uri(YoutrackUrl) };
			http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", YoutrackToken);
			http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

			var usersResp = await http.GetAsync("api/users?$top=1000&fields=id,login,name,banned,email");
			usersResp.EnsureSuccessStatusCode();
			var usersJson = await usersResp.Content.ReadAsStringAsync();
			var usersArray = JArray.Parse(usersJson);

			// Формируем словарь всех активных пользователей по email
			foreach (var user in usersArray)
			{
				if (user["banned"]?.ToObject<bool>() == true) continue;
				var email = user["email"]?.ToString();
				if (string.IsNullOrWhiteSpace(email)) continue;
				totals[email] = 0;
			}

			// 3. Получаем workItems за день
			var dateStr = today.ToString("yyyy-MM-dd");
			var workUrl = $"api/workItems?fields=author(email),duration(minutes)&$top=1000&startDate={dateStr}&endDate={dateStr}";
			var workResp = await http.GetAsync(workUrl);
			workResp.EnsureSuccessStatusCode();
			var workJson = await workResp.Content.ReadAsStringAsync();
			var items = JArray.Parse(workJson);

			foreach (var item in items)
			{
				var email = item["author"]?["email"]?.ToString();
				var mins = item["duration"]?["minutes"]?.ToObject<int>() ?? 0;
				if (string.IsNullOrWhiteSpace(email)) continue;

				if (!totals.ContainsKey(email))
					totals[email] = 0; // пользователь не найден в списке, но засчитаем

				totals[email] += mins;
			}

			return (absent, totals);
		}

		static async Task SendTextAsync(string login, string message, IHttpClientFactory httpFactory)
		{
			var buttons = new List<Button>
			{
				new Button(text: "Указать номер задачи", phrase: null, callbackData: new { action = "selectIssueNumber" }),
				new Button(text: "Создать отсутствие на сегодня", phrase: null, callbackData: new { action = "createAbsenceToday" }),
				new Button(text: "Получить список задач", phrase: null, callbackData: new { action = "getListOfIssues" }),

			};

			// ответ
			var reply = new
			{
				login,
				text = $"{message}: {login}",
				inline_keyboard = buttons
			};

			var client = httpFactory.CreateClient("YandexMessengerBot");
			var payload = JsonSerializer.Serialize(reply, new JsonSerializerOptions
			{
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
				TypeInfoResolver = new DefaultJsonTypeInfoResolver()
			});

			var response = await client.PostAsync("/bot/v1/messages/sendText/", new StringContent(payload, Encoding.UTF8, "application/json")
			);

			if (!response.IsSuccessStatusCode)
			{
				var err = await response.Content.ReadAsStringAsync();
				Console.Error.WriteLine($"[ERROR sendText] {response.StatusCode}: {err}");
			}
			else
			{
				Console.WriteLine("[OK] Ответ отправлен");
			}
		}

		static async Task CheckWorkTimeAndNotify(IHttpClientFactory httpFactory)
		{
			HashSet<string> TestEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				"",
			};
			var (absent, totals) = await GetWorkReportAsync();

			// Если указан список TestEmails, фильтруем только по ним
			var candidates = TestEmails != null && TestEmails.Count > 0
					? totals.Where(p => TestEmails.Contains(p.Key))
					: totals;


			foreach (var pair in candidates)
			{
				var email = pair.Key;
				var minutes = pair.Value;
				if (absent.Contains(email) || minutes >= ThresholdMinutes)
					continue;

				await SendTextAsync(email, "Работай ухахахах", httpFactory);
			}
		}

		static async Task CreateAbsenceForTodayAsync(string login)
		{
			var today = DateTime.UtcNow.Date;
			using var conn = new SqlConnection(SqlConnectionString);
			await conn.OpenAsync();

			// Получаем ID сотрудника по логину
			var getIdCmd = new SqlCommand(
				"SELECT ID FROM em.Employee WHERE Email = @login", conn);
			getIdCmd.Parameters.AddWithValue("@login", login);
			var result = await getIdCmd.ExecuteScalarAsync();
			if (result == null)
				throw new Exception($"Сотрудник с логином '{login}' не найден.");
			var empId = Convert.ToInt32(result);

			var getAbsenceTypeCmd = new SqlCommand(
				"SELECT ID FROM hr.AbsenceType WHERE Name = @AbsenceType", conn);
			getAbsenceTypeCmd.Parameters.AddWithValue("@AbsenceType", "Отсутствие");
			var absenceObj = await getAbsenceTypeCmd.ExecuteScalarAsync();
			if (absenceObj == null)
				throw new Exception("Тип отсутствия 'Отсутствие' не найден.");
			var absenceId = Convert.ToInt32(absenceObj);

			// Вставляем запись в hr.ActualAbsence
			var insertCmd = new SqlCommand(
				@"INSERT INTO hr.ActualAbsence (ID_em_Employee, ID_AbsenceType, DateBegin, DateEnd, Duration)
			VALUES (@empId, @absenceId, @dateBegin, @dateEnd, @duration)", conn);
			insertCmd.Parameters.AddWithValue("@empId", empId);
			insertCmd.Parameters.AddWithValue("@absenceId", absenceId);
			insertCmd.Parameters.AddWithValue("@dateBegin", today);
			insertCmd.Parameters.AddWithValue("@dateEnd", today);
			insertCmd.Parameters.AddWithValue("@duration", 1);

			await insertCmd.ExecuteNonQueryAsync();
		}

		string ExtractLogin(string emailOrLogin)
		{
			if (string.IsNullOrEmpty(emailOrLogin))
				return emailOrLogin;

			var atPos = emailOrLogin.IndexOf('@');
			if (atPos <= 0) // нет '@' или стоит в начале
				return emailOrLogin;

			return emailOrLogin.Substring(0, atPos);
		}
	}
}
public class Button
{
	[JsonPropertyName("text")]
	public string Text { get; set; }

	[JsonPropertyName("phrase")]
	public string Phrase { get; set; }

	[JsonPropertyName("callback_data")]
	public object CallbackData { get; set; }

	public Button(string text, string phrase = null, object callbackData = null)
	{
		Text = text;
		Phrase = phrase;
		CallbackData = callbackData;
	}

}
