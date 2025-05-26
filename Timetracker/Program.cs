using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using Microsoft.Data.SqlClient;

class Program
{
	private static readonly string youtrackUrl = ""; //
	private static readonly string token = "perm:"; //
	static async Task<HashSet<string>> GetAbsentEmployeesAsync(DateTime date)
	{
		var today = date.Date;
		var connectionString = ""; //

		var result = new HashSet<string>();

		using (var connection = new SqlConnection(connectionString))
		{
			await connection.OpenAsync();

			var query = @"
            SELECT p.Email
            FROM hr.ActualAbsence a
            JOIN em.Employee p ON a.ID_em_Employee = p.ID
            WHERE @today BETWEEN CAST(a.DateBegin AS date) AND CAST(a.DateEnd AS date)";

			using (var command = new SqlCommand(query, connection))
			{
				command.Parameters.AddWithValue("@today", today);

				using (var reader = await command.ExecuteReaderAsync())
				{
					while (await reader.ReadAsync())
					{
						var name = reader.GetString(0);
						result.Add(name);
					}
				}
			}
		}
		return result;
	}

	static async Task Main(string[] args)
	{
		// Определяем сегодняшнюю дату в формате yyyy-MM-dd
		var dateToday = DateTime.UtcNow.Date;
		string startDate = dateToday.ToString("yyyy-MM-dd");

		var absentEmployees = await GetAbsentEmployeesAsync(dateToday);

		using var client = new HttpClient();
		client.BaseAddress = new Uri(youtrackUrl);
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
		client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

		var usersResponse = await client.GetAsync("api/users?$top=1000&fields=id,login,name,banned,email");
		usersResponse.EnsureSuccessStatusCode();
		var usersJson = await usersResponse.Content.ReadAsStringAsync();
		var usersArray = JArray.Parse(usersJson);

		// Словарь для всех пользователей (id -> name)
		var allUsers = usersArray
			.Where(u => u["banned"]?.ToObject<bool>() != true)
			.ToDictionary(
				u => u["id"]?.ToString() ?? "unknown",
				u => $"{u["id"]?.ToString()} {u["name"]?.ToString() + " " + u["email"] ?? "unknown"}"
			);
		// Правильный URL: интерполируем login и экранируем его
		var safeLogin = Uri.EscapeDataString(""); //
												  // Запрос workItems за сегодня с информацией об issue
		string queryUrl = $"api/workItems?" +
	$"fields=issue(id,summary),author(id,login,name,email),duration(minutes)" +
	$"&$top=1000" +
	$"&startDate={startDate}" +
	$"&endDate={startDate}" + $"&author="; //

		var response = await client.GetAsync(queryUrl);
		if (!response.IsSuccessStatusCode)
		{
			var error = await response.Content.ReadAsStringAsync();
			Console.WriteLine("Ошибка при запросе workItems:\n" + error);
			return;
		}

		var json = await response.Content.ReadAsStringAsync();
		var items = JArray.Parse(json);

		// Структура: issue -> автор -> время
		var report = new Dictionary<string, Dictionary<string, int>>();
		var issueNames = new Dictionary<string, string>();

		// Новый словарь для суммарного времени каждого пользователя
		var totalByUser = allUsers.ToDictionary(pair => pair.Value, pair => 0);

		foreach (var item in items)
		{
			var email = item["email"];
			var issue = item["issue"];
			var author = item["author"];
			if (issue == null || author == null) continue;

			var issueId = issue["id"]?.ToString() ?? "unknown";
			var issueSummary = issue["summary"]?.ToString() ?? issueId;
			issueNames[issueId] = issueSummary;

			var authorId = author["id"]?.ToString();
			if (authorId == null || !allUsers.ContainsKey(authorId)) continue;

			var authorName = allUsers[authorId];
			int minutes = item["duration"]?["minutes"]?.ToObject<int>() ?? 0;

			// В отчёт по задачам
			if (!report.ContainsKey(issueId))
				report[issueId] = new Dictionary<string, int>();
			if (!report[issueId].ContainsKey(authorName))
				report[issueId][authorName] = 0;
			report[issueId][authorName] += minutes;

			// В общее по пользователю
			totalByUser[authorName] += minutes;
		}

		// Отчёт по задачам
		Console.WriteLine($"Отчёт по работам сотрудников за {dateToday:yyyy-MM-dd} по задачам:\n");
		foreach (var issueEntry in report)
		{
			var id = issueEntry.Key;
			Console.WriteLine($"Issue {id} — {issueNames[id]}");
			foreach (var userEntry in issueEntry.Value.OrderByDescending(u => u.Value))
			{
				Console.WriteLine($"  {userEntry.Key,-25} — {userEntry.Value,6} мин");
			}
			Console.WriteLine();
		}

		// Вывод суммарного времени и проверка на 7 часов
		// Отчёт по пользователям
		Console.WriteLine("Суммарное время по каждому сотруднику:");
		const int thresholdMinutes = 7 * 60;
		foreach (var userEntry in totalByUser.OrderBy(u => u.Key))
		{
			var name = userEntry.Key;
			var total = userEntry.Value;

			// Признак отсутствия
			bool isAbsent = absentEmployees.Any(abs => name.Contains(abs));

			Console.Write($"  {name,-80} — {total,3} мин");

			if (isAbsent)
				Console.WriteLine("  <-- в отпуске/отсутствует ");
			else if (total < thresholdMinutes)
				Console.WriteLine(" - <-- работал(а) меньше 7 часов! ");
			else
				Console.WriteLine(" + <-- отработал(а) 7 часов! ");
		}
	}
}