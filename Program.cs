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
const int SeriesYearQuota = 50;
// Yearly composition: 80% of the quota is the most voted titles with a RU
// description, the rest are the highest-rated of the year's top-200 by votes
// that missed the votes part. Candidates are overfetched to the whole
// top-200 so the rating picks (and description backfill) are covered.
const int PoolPerYear = 200;
// Kids movies: a top of their own on the same principle, 25 a year (20 by
// votes + 5 by rating). The pool is the year's most voted movies whose
// genres could pass the kids rule; the age rating is checked after TMDB.
// Fewer than 25 when the year has no more with a description and a poster.
const int KidsYearQuota = 25;
const int KidsPoolPerYear = 100;
const int KidsMinVotes = 1000;

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

Console.WriteLine($"MovieClassics: фильмы {MovieFirstYear}+, сериалы {SeriesFirstYear}+ " +
                  $"(по {MovieYearQuota}/год: 40 по голосам + 10 по рейтингу из топ-{PoolPerYear}); {currentYear}: " +
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

var kindConfigs = new[]
{
    (Kind: "movie", Quota: MovieYearQuota, FirstYear: movieFirst),
    (Kind: "series", Quota: SeriesYearQuota, FirstYear: seriesFirst)
};

// Pass 1: the whole top-200 of each year by votes; the votes/rating split
// and the description cut happen after TMDB enrichment.
var candidates = new Dictionary<string, List<TitleRow>>();
foreach (var (kind, quota, firstYear) in kindConfigs)
{
    var picked = new List<TitleRow>();
    foreach (var year in Enumerable.Range(firstYear, currentYear - firstYear + 1).OrderBy(y => y))
    {
        if (!byYear.TryGetValue(kind, out var rows) || !rows.TryGetValue(year, out var list)) continue;
        list.Sort((a, b) => b.Votes.CompareTo(a.Votes));
        picked.AddRange(list.Take(PoolPerYear));
    }
    candidates[kind] = picked;
    Console.WriteLine($"  {kind}: {picked.Count:N0} кандидатов (топ-{PoolPerYear} года)");
}

var kidsCandidates = new List<TitleRow>();
foreach (var year in Enumerable.Range(movieFirst, currentYear - movieFirst + 1))
{
    if (!byYear.TryGetValue("movie", out var rows) || !rows.TryGetValue(year, out var list)) continue;
    kidsCandidates.AddRange(list // votes desc already
        .Where(r => r.Votes >= KidsMinVotes && MaybeForKids(r))
        .Take(KidsPoolPerYear));
}
Console.WriteLine($"  kids: {kidsCandidates.Count:N0} кандидатов (топ-{KidsPoolPerYear} года по жанрам)");

// ---- TMDB enrichment (cached) ----------------------------------------------

var cachePath = Path.Combine(cacheDir, "tmdb-cache.json");
// Web defaults: the file is written camelCase, and matching must be
// case-INsensitive — the case-sensitive default deserializer silently
// produced all-null shells out of perfectly good entries (the "shell
// incident": every run refetched everything it needed).
var cacheJsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var cache = File.Exists(cachePath)
    ? JsonSerializer.Deserialize<Dictionary<string, TmdbEntry>>(File.ReadAllText(cachePath), cacheJsonOpts) ?? new()
    : new Dictionary<string, TmdbEntry>();
Console.WriteLine($"  кэш: {cache.Count:N0} записей, " +
                  $"{cache.Values.Count(e => e.TmdbId == null && !e.NotFound):N0} пустых, " +
                  $"{cache.Values.Count(e => e.ReleaseDate != null):N0} с датой");

var work = new List<(TitleRow Row, bool DateOnly, TmdbEntry? Entry)>();
foreach (var row in candidates.Values.SelectMany(l => l).Concat(kidsCandidates).DistinctBy(r => r.ImdbId))
{
    if (!cache.TryGetValue(row.ImdbId, out var entry) || (entry.TmdbId == null && !entry.NotFound))
    {
        // No entry, or an empty shell (no data and no NotFound marker) —
        // treat as missing so the run refetches instead of publishing blanks.
        work.Add((row, false, null));
    }
    else if (entry.TmdbId != null && !entry.NotFound && entry.ReleaseDate == null && !entry.NoReleaseDate)
    {
        // Enriched before release dates were kept — one light details call.
        work.Add((row, true, entry));
    }
}
Console.WriteLine($"TMDB: {work.Count(w => !w.DateOnly):N0} новых титлов, " +
                  $"{work.Count(w => w.DateOnly):N0} дат, {cache.Count:N0} в кэше");

var done = 0;
var gate = new SemaphoreSlim(4);
var saveLock = new object();
var tasks = work.Select(async w =>
{
    await gate.WaitAsync();
    try
    {
        if (w.DateOnly)
        {
            await FetchReleaseDateAsync(http, w.Row, w.Entry!);
        }
        else
        {
            var entry = await FetchTmdbAsync(http, w.Row);
            lock (saveLock) { cache[w.Row.ImdbId] = entry; }
        }
        var n = Interlocked.Increment(ref done);
        if (n % 500 == 0)
        {
            Console.Write($"\r  TMDB {n}/{work.Count}");
            lock (saveLock) { SaveCache(cachePath, cache); }
        }
    }
    catch (Exception ex)
    {
        // Network hiccups: leave the title uncached so the next run retries it.
        Console.WriteLine($"\n  ! {w.Row.ImdbId} {w.Row.Title}: {ex.Message}");
    }
    finally { gate.Release(); }
}).ToList();
await Task.WhenAll(tasks);
Console.WriteLine($"\r  TMDB {done}/{work.Count} готово   ");
Console.WriteLine($"  перед сохранением кэша: {cache.Count:N0} записей");
SaveCache(cachePath, cache);

// Pass 2: 80% of the yearly quota = most voted titles with a RU description;
// the rest = highest-rated of the year's top-200 by votes that missed the
// votes part. Within a year everything is ordered by release date (undated
// last), votes descending as the tie-break.
var selected = new Dictionary<string, List<TitleRow>>();
foreach (var (kind, quota, firstYear) in kindConfigs)
{
    var final = new List<TitleRow>();
    var droppedNoDescription = 0;
    var totalVotesPart = 0;
    var totalRatingPart = 0;

    foreach (var year in Enumerable.Range(firstYear, currentYear - firstYear + 1).OrderBy(y => y))
    {
        var quotaY = YearQuota(quota, year);
        var votesQuota = (int)Math.Round(quotaY * 0.8);
        var ratingQuota = quotaY - votesQuota;

        var pool = candidates[kind].Where(r => r.Year == year).ToList(); // votes desc already
        bool HasRuDescription(TitleRow r) =>
            cache.TryGetValue(r.ImdbId, out var e) && !string.IsNullOrWhiteSpace(e.OverviewRu);
        string ReleaseDateOf(TitleRow r) =>
            cache.TryGetValue(r.ImdbId, out var e) ? e.ReleaseDate ?? "9999-99-99" : "9999-99-99";

        var votesPart = new List<TitleRow>();
        foreach (var row in pool)
        {
            if (votesPart.Count == votesQuota) break;
            if (HasRuDescription(row)) votesPart.Add(row);
            else droppedNoDescription++;
        }

        var ratingPart = pool.Except(votesPart)
            .OrderByDescending(r => r.Rating)
            .ThenByDescending(r => r.Votes)
            .Where(HasRuDescription)
            .Take(ratingQuota)
            .ToList();

        totalVotesPart += votesPart.Count;
        totalRatingPart += ratingPart.Count;
        final.AddRange(votesPart.Concat(ratingPart)
            .OrderBy(ReleaseDateOf)
            .ThenByDescending(r => r.Votes));
    }

    selected[kind] = final;
    Console.WriteLine($"  {kind}: {final.Count:N0} отобрано ({totalVotesPart} по голосам + {totalRatingPart} по рейтингу), " +
                      $"{droppedNoDescription} без описания исключено");
}

// ---- age ratings (cached; only titles that made the tops) -------------------

var certWork = selected.Values.SelectMany(l => l).Concat(kidsCandidates).DistinctBy(r => r.ImdbId)
    .Where(r => cache.TryGetValue(r.ImdbId, out var e) && e.TmdbId != null && e.Certifications == null)
    .ToList();
Console.WriteLine($"Возрастные рейтинги: {certWork.Count:N0} новых");
var certDone = 0;
await Task.WhenAll(certWork.Select(async row =>
{
    await gate.WaitAsync();
    try
    {
        var entry = cache[row.ImdbId];
        entry.Certifications = await FetchCertificationsAsync(http, row, entry.TmdbId!.Value);
        if (Interlocked.Increment(ref certDone) % 500 == 0)
        {
            Console.Write($"\r  рейтинги {certDone}/{certWork.Count}");
            lock (saveLock) { SaveCache(cachePath, cache); }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  ! {row.ImdbId} рейтинг: {ex.Message}");
    }
    finally { gate.Release(); }
}));
SaveCache(cachePath, cache);
Console.WriteLine($"\r  рейтинги {certDone}/{certWork.Count} готово   ");

// Kids top: 80% most voted, the rest highest rated of the remaining pool;
// only titles the age rating confirms, with a RU description and a poster.
var kidsSelected = new List<TitleRow>();
foreach (var year in Enumerable.Range(movieFirst, currentYear - movieFirst + 1))
{
    var quotaY = YearQuota(KidsYearQuota, year);
    var votesQuota = (int)Math.Round(quotaY * 0.8);
    var pool = kidsCandidates
        .Where(r => r.Year == year && cache.TryGetValue(r.ImdbId, out var e) &&
                    !string.IsNullOrWhiteSpace(e.OverviewRu) && e.PosterPath != null &&
                    IsForKids(r, e.Certifications))
        .ToList();
    var votesPart = pool.Take(votesQuota).ToList();
    var ratingPart = pool.Skip(votesQuota)
        .OrderByDescending(r => r.Rating)
        .ThenByDescending(r => r.Votes)
        .Take(quotaY - votesPart.Count)
        .ToList();
    kidsSelected.AddRange(votesPart.Concat(ratingPart)
        .OrderBy(r => cache[r.ImdbId].ReleaseDate ?? "9999-99-99")
        .ThenByDescending(r => r.Votes));
}
Console.WriteLine($"  kids: {kidsSelected.Count:N0} отобрано");

// ---- output ----------------------------------------------------------------

var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
    // Keep Cyrillic as UTF-8: \uXXXX escaping roughly doubles the payload.
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

selected["kids"] = kidsSelected;
foreach (var (kind, fileName) in new[] { ("movie", "movies.json"), ("series", "series.json"), ("kids", "kids-movies.json") })
{
    var items = new List<Dictionary<string, object?>>();
    foreach (var row in selected[kind]) // year asc, release date asc inside a year
    {
        cache.TryGetValue(row.ImdbId, out var tmdb);
        items.Add(new Dictionary<string, object?>
        {
            ["imdbId"] = row.ImdbId,
            ["tmdbId"] = tmdb?.TmdbId,
            ["kind"] = kind == "series" ? "сериал" : "фильм",
            ["year"] = row.Year,
            ["releaseDate"] = tmdb?.ReleaseDate,
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

    // App display shape: only the fields it shows.
    static Dictionary<string, object?> ToAppShape(Dictionary<string, object?> m) => new()
    {
        ["imdbId"] = m["imdbId"],
        ["year"] = m["year"],
        ["releaseDate"] = m["releaseDate"],
        ["rating"] = m["rating"],
        ["votes"] = m["votes"],
        ["titleOriginal"] = m["titleOriginal"],
        ["titleEn"] = m["titleEn"],
        ["titleRu"] = m["titleRu"],
        ["overviewRu"] = m["overviewRu"],
        ["posterSmall"] = m["posterSmall"]
    };

    var slimPath = Path.Combine(docsDir, "data", fileName.Replace(".json", "-slim.json"));
    File.WriteAllText(slimPath, JsonSerializer.Serialize(items.Select(ToAppShape), jsonOpts));
    Console.WriteLine($"  {Path.GetFileName(slimPath)}: {items.Count:N0} записей");

    // Tail with just the newest 50: the app shows it instantly while the
    // full file downloads in the background.
    var latestPath = Path.Combine(docsDir, "data", fileName.Replace(".json", "-latest.json"));
    var latest = items.Skip(Math.Max(0, items.Count - 50)).Select(ToAppShape);
    File.WriteAllText(latestPath, JsonSerializer.Serialize(latest, jsonOpts));
    Console.WriteLine($"  {Path.GetFileName(latestPath)}: {items.Skip(Math.Max(0, items.Count - 50)).Count():N0} записей");
}

var meta = new
{
    generatedAt = DateTime.UtcNow.ToString("o"),
    movies = selected["movie"].Count,
    series = selected["series"].Count,
    kidsMovies = kidsSelected.Count,
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

    // Cache entries have gone missing between runs before (the shell
    // incident), so verify the write actually holds everything.
    var reloaded = JsonSerializer.Deserialize<Dictionary<string, TmdbEntry>>(File.ReadAllText(path), opts);
    if (reloaded == null || reloaded.Count != cache.Count)
    {
        Console.WriteLine($"  !! SAVECACHE MISMATCH: wrote {cache.Count:N0}, file holds {reloaded?.Count ?? 0:N0}");
    }
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

        var genres = f.Length > 8 && f[8] != @"\N" ? f[8].Split(',') : Array.Empty<string>();
        yield return new TitleRow(f[0], kind, year, f[2], f[3] == @"\N" ? f[2] : f[3], v.Rating, v.Count, genres);
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

    entry.ReleaseDate = FirstNonEmpty(
        row.Kind == "movie" ? ru?.ReleaseDate : ru?.FirstAirDate,
        row.Kind == "movie" ? hit.ReleaseDate : hit.FirstAirDate);
    entry.NoReleaseDate = entry.ReleaseDate == null;

    return entry;
}

// One light details call for entries enriched before release dates were kept.
static async Task FetchReleaseDateAsync(HttpClient client, TitleRow row, TmdbEntry entry)
{
    var kindPath = row.Kind == "movie" ? "movie" : "tv";
    var ru = await client.GetFromJsonAsync<DetailsResponse>(
        $"https://api.themoviedb.org/3/{kindPath}/{entry.TmdbId}?api_key={TmdbKey}&language=ru-RU");
    entry.ReleaseDate = row.Kind == "movie" ? ru?.ReleaseDate : ru?.FirstAirDate;
    entry.NoReleaseDate = string.IsNullOrWhiteSpace(entry.ReleaseDate);
}

// Age ratings from TMDB for the countries whose systems say "for children".
static async Task<List<string>> FetchCertificationsAsync(HttpClient client, TitleRow row, int tmdbId)
{
    var result = new List<string>();
    var wanted = new HashSet<string> { "US", "RU", "DE", "GB" };
    if (row.Kind == "movie")
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync(
            $"https://api.themoviedb.org/3/movie/{tmdbId}/release_dates?api_key={TmdbKey}"));
        foreach (var country in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var code = country.GetProperty("iso_3166_1").GetString() ?? "";
            if (!wanted.Contains(code)) continue;
            foreach (var release in country.GetProperty("release_dates").EnumerateArray())
            {
                var cert = release.GetProperty("certification").GetString();
                if (!string.IsNullOrWhiteSpace(cert)) result.Add($"{code}:{cert.Trim()}");
            }
        }
    }
    else
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync(
            $"https://api.themoviedb.org/3/tv/{tmdbId}/content_ratings?api_key={TmdbKey}"));
        foreach (var country in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var code = country.GetProperty("iso_3166_1").GetString() ?? "";
            var rating = country.GetProperty("rating").GetString();
            if (wanted.Contains(code) && !string.IsNullOrWhiteSpace(rating)) result.Add($"{code}:{rating.Trim()}");
        }
    }
    return result.Distinct().ToList();
}

// Children's titles: an age rating that says so; PG only together with a
// family/animation genre (old PG films include Jaws); without any rating a
// family genre with nothing dark in it. Animation alone is not enough —
// plenty of anime is for adults.
static bool IsForKids(TitleRow row, List<string>? certs)
{
    var dark = row.Genres.Any(g => g is "Horror" or "Crime" or "War" or "Thriller");
    var family = row.Genres.Contains("Family");
    var familyish = family || row.Genres.Contains("Animation");
    // A mild rating alone means "safe", not "for children": old dramas and
    // westerns are rated G on re-release, car shows TV-G. Without a family
    // or animation genre it needs a light genre and nothing grown-up.
    var light = row.Genres.Any(g => g is "Adventure" or "Fantasy" or "Comedy" or "Musical");
    var grownUp = row.Genres.Any(g => g is "Drama" or "Biography" or "History" or "Western" or "Documentary"
        or "Romance" or "Talk-Show" or "Reality-TV" or "News" or "Game-Show" or "Sport");
    if (certs is { Count: > 0 })
    {
        if (certs.Any(c => c is "US:G" or "US:TV-Y" or "US:TV-Y7" or "US:TV-G" or "RU:0+" or "RU:6+" or "DE:0" or "GB:U"))
        {
            return !dark && (familyish || (light && !grownUp));
        }
        if (certs.Any(c => c is "US:PG" or "DE:6" or "GB:PG"))
        {
            return familyish && !dark;
        }
        // Rated, but only for teens or adults.
        if (certs.Any(c => c.StartsWith("US:") || c.StartsWith("RU:") || c.StartsWith("DE:") || c.StartsWith("GB:")))
        {
            return false;
        }
    }
    return family && !dark;
}

// Genres alone could still make a kids title (before the age rating is known).
static bool MaybeForKids(TitleRow row)
{
    var g = row.Genres;
    if (g.Any(x => x is "Horror" or "Crime" or "War" or "Thriller")) return false;
    var light = g.Any(x => x is "Adventure" or "Fantasy" or "Comedy" or "Musical");
    var grownUp = g.Any(x => x is "Drama" or "Biography" or "History" or "Western" or "Documentary"
        or "Romance" or "Talk-Show" or "Reality-TV" or "News" or "Game-Show" or "Sport");
    return g.Contains("Family") || g.Contains("Animation") || (light && !grownUp);
}

static string? FirstNonEmpty(string? first, string? second) =>
    !string.IsNullOrWhiteSpace(first) ? first : (string.IsNullOrWhiteSpace(second) ? null : second);

internal sealed record TitleRow(string ImdbId, string Kind, int Year, string Title, string OriginalTitle, double Rating, int Votes, string[] Genres);

internal sealed class TmdbEntry
{
    public int? TmdbId { get; set; }
    public string? PosterPath { get; set; }
    public string? TitleEn { get; set; }
    public string? TitleRu { get; set; }
    public string? OverviewEn { get; set; }
    public string? OverviewRu { get; set; }
    // "YYYY-MM-DD"; NoReleaseDate marks titles whose TMDB entry has none,
    // so the date pass doesn't retry them every run.
    public string? ReleaseDate { get; set; }
    public bool NoReleaseDate { get; set; }
    public bool NotFound { get; set; }
    // Age ratings as "US:PG", "RU:6+", "DE:0"…; null = not fetched yet.
    public List<string>? Certifications { get; set; }
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
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; set; }
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
