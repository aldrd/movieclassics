// MovieClassics — builder of the "cinema classics" tables.
//
//   IMDb: official bulk datasets (title.basics + title.ratings) give ratings
//         and vote counts for every title; the top of each year is taken by
//         the number of votes.
//   TMDB: find by IMDb id, then details in ru/en — poster path, Russian and
//         English titles/descriptions. Cached locally so monthly runs only
//         fetch what is new.
//
// Output: docs/ with data/movies.json, data/series.json, data/meta.json and
// index.html — ready to be published to GitHub Pages.

using System.IO.Compression;
using System.Net.Http.Json;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// ---- configuration --------------------------------------------------------

const string TmdbKey = "ae8be8be239638a89b02d125bf8d5458";
const int MovieFirstYear = 1937;
const int MovieYearQuota = 50;
const int SeriesFirstYear = 1980;
const int SeriesYearQuota = 20;

var smoke = args.Contains("--smoke"); // small run to validate the pipeline fast

var root = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(root, "MovieClassics.csproj")))
{
    var parent = Path.GetDirectoryName(root);
    if (parent == null) throw new InvalidOperationException("project root not found");
    root = parent;
}
var cacheDir = Path.Combine(root, "cache");
var docsDir = Path.Combine(root, "docs");
Directory.CreateDirectory(cacheDir);
Directory.CreateDirectory(Path.Combine(docsDir, "data"));

var currentYear = DateTime.UtcNow.Year;
var yearFraction = Math.Clamp(
    DateTime.UtcNow.DayOfYear / (DateTime.IsLeapYear(DateTime.UtcNow.Year) ? 366.0 : 365.0),
    0.0, 1.0);

int YearQuota(int quota, int year) =>
    year == currentYear ? Math.Max(1, (int)Math.Round(quota * yearFraction)) : quota;

Console.WriteLine($"MovieClassics: фильмы {MovieFirstYear}+ (по {MovieYearQuota}/год), " +
                  $"сериалы {SeriesFirstYear}+ (по {SeriesYearQuota}/год); {currentYear}: " +
                  $"{YearQuota(MovieYearQuota, currentYear)} фильмов / {YearQuota(SeriesYearQuota, currentYear)} сериалов " +
                  $"({yearFraction:P0} года прошло){(smoke ? " [SMOKE]" : "")}");

using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
http.Timeout = TimeSpan.FromSeconds(30);
http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieClassics/1.0");

// ---- IMDb datasets ---------------------------------------------------------

var basicsPath = Path.Combine(cacheDir, "title.basics.tsv.gz");
var ratingsPath = Path.Combine(cacheDir, "title.ratings.tsv.gz");
await DownloadIfNeededAsync("title.basics.tsv.gz", basicsPath);
await DownloadIfNeededAsync("title.ratings.tsv.gz", ratingsPath);

Console.WriteLine("Читаю рейтинги…");
var votes = new Dictionary<string, (double Rating, int Count)>();
foreach (var (tconst, rating, count) in ReadRatings(ratingsPath))
{
    votes[tconst] = (rating, count);
}
Console.WriteLine($"  {votes.Count:N0} титлов с рейтингом");

Console.WriteLine("Читаю базу титлов…");
int movieFirst = smoke ? currentYear - 2 : MovieFirstYear;
int seriesFirst = smoke ? currentYear - 2 : SeriesFirstYear;
var byYear = new Dictionary<string, Dictionary<int, List<TitleRow>>>(); // kind → year → rows
foreach (var row in ReadBasics(basicsPath, votes, movieFirst, seriesFirst, currentYear))
{
    var years = byYear.GetOrAdd(row.Kind, () => new Dictionary<int, List<TitleRow>>());
    years.GetOrAdd(row.Year, () => new List<TitleRow>()).Add(row);
}

var selected = new Dictionary<string, List<TitleRow>>();
foreach (var (kind, quota, firstYear) in new[]
         {
             ("movie", MovieYearQuota, movieFirst),
             ("series", SeriesYearQuota, seriesFirst)
         })
{
    var picked = new List<TitleRow>();
    foreach (var year in Enumerable.Range(firstYear, currentYear - firstYear + 1).OrderBy(y => y))
    {
        if (!byYear.TryGetValue(kind, out var rows) || !rows.TryGetValue(year, out var list)) continue;
        list.Sort((a, b) => b.Votes.CompareTo(a.Votes));
        picked.AddRange(list.Take(YearQuota(quota, year)));
    }
    selected[kind] = picked;
    Console.WriteLine($"  {kind}: {picked.Count:N0} отобрано ({picked.Select(r => r.Year).Distinct().Count()} лет)");
}

// ---- TMDB enrichment (cached) ----------------------------------------------

var cachePath = Path.Combine(cacheDir, "tmdb-cache.json");
var cache = File.Exists(cachePath)
    ? JsonSerializer.Deserialize<Dictionary<string, TmdbEntry>>(File.ReadAllText(cachePath)) ?? new()
    : new Dictionary<string, TmdbEntry>();

var missing = selected.Values.SelectMany(l => l)
    .Where(r => !cache.ContainsKey(r.ImdbId)).ToList();
Console.WriteLine($"TMDB: {missing.Count:N0} новых титлов, {cache.Count:N0} в кэше");

var done = 0;
var gate = new SemaphoreSlim(4);
var saveLock = new object();
var tasks = missing.Select(async row =>
{
    await gate.WaitAsync();
    try
    {
        var entry = await FetchTmdbAsync(http, row);
        lock (saveLock) { cache[row.ImdbId] = entry; }
        var n = Interlocked.Increment(ref done);
        if (n % 100 == 0)
        {
            Console.Write($"\r  TMDB {n}/{missing.Count}");
            lock (saveLock) { SaveCache(cachePath, cache); }
        }
    }
    catch (Exception ex)
    {
        // Network hiccups: leave the title uncached so the next run retries it.
        Console.WriteLine($"\n  ! {row.ImdbId} {row.Title}: {ex.Message}");
    }
    finally { gate.Release(); }
}).ToList();
await Task.WhenAll(tasks);
Console.WriteLine($"\r  TMDB {done}/{missing.Count} готово   ");
SaveCache(cachePath, cache);

// ---- output ----------------------------------------------------------------

var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false
};

foreach (var (kind, fileName) in new[] { ("movie", "movies.json"), ("series", "series.json") })
{
    var items = new List<Dictionary<string, object?>>();
    foreach (var row in selected[kind]) // already year asc, votes desc
    {
        cache.TryGetValue(row.ImdbId, out var tmdb);
        items.Add(new Dictionary<string, object?>
        {
            ["imdbId"] = row.ImdbId,
            ["tmdbId"] = tmdb?.TmdbId,
            ["kind"] = kind == "movie" ? "фильм" : "сериал",
            ["year"] = row.Year,
            ["rating"] = row.Rating,
            ["votes"] = row.Votes,
            ["titleOriginal"] = row.OriginalTitle,
            ["titleEn"] = tmdb?.TitleEn ?? row.Title,
            ["titleRu"] = tmdb?.TitleRu,
            ["overviewEn"] = tmdb?.OverviewEn,
            ["overviewRu"] = tmdb?.OverviewRu,
            ["poster"] = tmdb?.PosterPath is null ? null : "https://image.tmdb.org/t/p/w500" + tmdb.PosterPath,
            ["posterSmall"] = tmdb?.PosterPath is null ? null : "https://image.tmdb.org/t/p/w185" + tmdb.PosterPath
        });
    }
    var path = Path.Combine(docsDir, "data", fileName);
    File.WriteAllText(path, JsonSerializer.Serialize(items, jsonOpts));
    Console.WriteLine($"  {fileName}: {items.Count:N0} записей");
}

var meta = new
{
    generatedAt = DateTime.UtcNow.ToString("o"),
    movies = selected["movie"].Count,
    series = selected["series"].Count,
    movieFirstYear = movieFirst,
    seriesFirstYear = seriesFirst,
    currentYearQuotaMovies = YearQuota(MovieYearQuota, currentYear),
    currentYearQuotaSeries = YearQuota(SeriesYearQuota, currentYear)
};
File.WriteAllText(Path.Combine(docsDir, "data", "meta.json"), JsonSerializer.Serialize(meta, jsonOpts));

Console.WriteLine("Готово: docs/data/*.json");

// ---- helpers ---------------------------------------------------------------

async Task DownloadIfNeededAsync(string name, string target)
{
    if (File.Exists(target) && File.GetLastWriteTimeUtc(target) > DateTime.UtcNow.AddDays(-20))
    {
        Console.WriteLine($"{name}: кэш свежий");
        return;
    }

    Console.Write($"Качаю {name}… ");
    using var resp = await http.GetAsync($"https://datasets.imdbws.com/{name}", HttpCompletionOption.ResponseHeadersRead);
    resp.EnsureSuccessStatusCode();
    await using var fs = File.Create(target);
    await resp.Content.CopyToAsync(fs);
    Console.WriteLine($"{new FileInfo(target).Length / 1024 / 1024} МБ");
}

static void SaveCache(string path, Dictionary<string, TmdbEntry> cache)
{
    var tmp = path + ".tmp";
    var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    File.WriteAllText(tmp, JsonSerializer.Serialize(cache, opts));
    File.Move(tmp, path, overwrite: true);
}

static IEnumerable<(string Tconst, double Rating, int Count)> ReadRatings(string path)
{
    using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
    using var reader = new StreamReader(gz, Encoding.UTF8);
    _ = reader.ReadLine(); // header
    while (reader.ReadLine() is { } line)
    {
        var f = line.Split('\t');
        if (f.Length < 3) continue;
        if (!int.TryParse(f[2], out var count) || count <= 0) continue;
        yield return (f[0], double.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture), count);
    }
}

static IEnumerable<TitleRow> ReadBasics(string path, Dictionary<string, (double Rating, int Count)> votes,
    int movieFirst, int seriesFirst, int currentYear)
{
    using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
    using var reader = new StreamReader(gz, Encoding.UTF8);
    _ = reader.ReadLine(); // header
    while (reader.ReadLine() is { } line)
    {
        var f = line.Split('\t');
        if (f.Length < 5) continue;

        string kind;
        int firstYear;
        if (f[1] == "movie") { kind = "movie"; firstYear = movieFirst; }
        else if (f[1] == "tvSeries") { kind = "series"; firstYear = seriesFirst; }
        else continue;

        if (f[5] == @"\N") continue; // startYear
        if (!int.TryParse(f[5], out var year) || year < firstYear || year > currentYear) continue;

        if (!votes.TryGetValue(f[0], out var v)) continue;

        yield return new TitleRow(f[0], kind, year, f[2], f[3] == @"\N" ? f[2] : f[3], v.Rating, v.Count);
    }
}

async Task<TmdbEntry> FetchTmdbAsync(HttpClient client, TitleRow row)
{
    var kindPath = row.Kind == "movie" ? "movie" : "tv";
    var found = await client.GetFromJsonAsync<FindResponse>(
        $"https://api.themoviedb.org/3/find/{row.ImdbId}?external_source=imdb_id&api_key={TmdbKey}&language=en-US");
    var hit = (row.Kind == "movie" ? found?.MovieResult : found?.TvResult)?.FirstOrDefault();
    if (hit == null || hit.Id == 0)
    {
        return new TmdbEntry { NotFound = true }; // negative marker: don't retry every month
    }

    var entry = new TmdbEntry
    {
        TmdbId = hit.Id,
        PosterPath = hit.PosterPath,
        TitleEn = row.Kind == "movie" ? hit.Title : hit.Name,
        OverviewEn = string.IsNullOrWhiteSpace(hit.Overview) ? null : hit.Overview
    };

    var ru = await client.GetFromJsonAsync<DetailsResponse>(
        $"https://api.themoviedb.org/3/{kindPath}/{hit.Id}?api_key={TmdbKey}&language=ru-RU");
    if (ru != null)
    {
        entry.TitleRu = row.Kind == "movie" ? ru.Title : ru.Name;
        entry.OverviewRu = string.IsNullOrWhiteSpace(ru.Overview) ? null : ru.Overview;
    }

    return entry;
}

internal sealed record TitleRow(string ImdbId, string Kind, int Year, string Title, string OriginalTitle, double Rating, int Votes);

internal sealed class TmdbEntry
{
    public int? TmdbId { get; set; }
    public string? PosterPath { get; set; }
    public string? TitleEn { get; set; }
    public string? TitleRu { get; set; }
    public string? OverviewEn { get; set; }
    public string? OverviewRu { get; set; }
    public bool NotFound { get; set; }
}

internal sealed class FindResponse
{
    [JsonPropertyName("movie_results")] public List<DetailsResponse>? MovieResult { get; set; }
    [JsonPropertyName("tv_results")] public List<DetailsResponse>? TvResult { get; set; }
}

internal sealed class DetailsResponse
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string? Overview { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
}

internal static class DictExtensions
{
    public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, Func<TValue> create)
        where TKey : notnull
    {
        if (dict.TryGetValue(key, out var value)) return value;
        value = create();
        dict[key] = value;
        return value;
    }
}
