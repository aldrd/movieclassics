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
// Titles rated below this never enter a top: the next candidate of the year
// takes the slot.
const double MinRating = 5.0;
// New releases are still gathering votes and would miss the top until they
// catch up: a title out less than five months with enough votes and a high
// rating enters its year's top on top of the quota.
const int FreshDays = 153;
const int FreshMinVotes = 1000;
const double FreshMinRating = 7.0;
// Russian tops from Kinopoisk (poiskkino.dev API), shown merged with the
// IMDb tops in the Russian UI: the same yearly principle, rated by KP.
const int RuMovieYearQuota = 20;
const int RuSeriesYearQuota = 10;
const int RuPoolPerYear = 100;
const int RuMinVotes = 1000;        // past years
const int RuMinVotesCurrent = 100;  // the current year is still gathering votes

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
// Votes change daily (new releases gain thousands a week), so the small
// ratings file is fetched every run; the big titles file only weekly — a
// title is listed there long before its release.
await DownloadIfNeededAsync("title.ratings.tsv.gz", ratingsPath, TimeSpan.FromHours(20));
await DownloadIfNeededAsync("title.basics.tsv.gz", basicsPath, TimeSpan.FromDays(7));

Console.WriteLine("Читаю рейтинги…");
var votes = new Dictionary<string, (double Rating, int Count)>();
foreach (var (tconst, rating, count) in ReadRatings(ratingsPath))
{
    votes[tconst] = (rating, count);
}
Console.WriteLine($"  {votes.Count:N0} титлов с рейтингом");

// ---- search catalog (one-off: dotnet run -- --catalog) -----------------------
// The most voted 20 000 movies and 10 000 series of all years, as a compact
// TSV the app ships for title hints: tt-id, year, m/s, votes, RU title, EN title,
// original title (empty when the same as EN), poster path.
if (args.Contains("--catalog"))
{
    const int CatalogMovies = 20_000;
    const int CatalogSeries = 10_000;
    var all = ReadBasics(basicsPath, votes, 1890, 1940, currentYear).ToList();
    var picks = all.Where(r => r.Kind == "movie").OrderByDescending(r => r.Votes).Take(CatalogMovies)
        .Concat(all.Where(r => r.Kind == "series").OrderByDescending(r => r.Votes).Take(CatalogSeries))
        .ToList();

    var catalogCachePath = Path.Combine(cacheDir, "tmdb-cache.json");
    var catalogCache = File.Exists(catalogCachePath)
        ? JsonSerializer.Deserialize<Dictionary<string, TmdbEntry>>(File.ReadAllText(catalogCachePath), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new()
        : new Dictionary<string, TmdbEntry>();
    var missing = picks.Where(r => !catalogCache.TryGetValue(r.ImdbId, out var e) || (e.TmdbId == null && !e.NotFound)).ToList();
    Console.WriteLine($"Каталог: {picks.Count:N0} титлов, {missing.Count:N0} без данных TMDB");

    var catalogGate = new SemaphoreSlim(8);
    var catalogLock = new object();
    var fetched = 0;
    await Task.WhenAll(missing.Select(async row =>
    {
        await catalogGate.WaitAsync();
        try
        {
            var entry = await FetchTmdbAsync(http, row);
            lock (catalogLock)
            {
                catalogCache[row.ImdbId] = entry;
                if (++fetched % 500 == 0)
                {
                    Console.Write($"\r  TMDB {fetched}/{missing.Count}");
                    SaveCache(catalogCachePath, catalogCache);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n  ! {row.ImdbId} {row.Title}: {ex.Message}");
        }
        finally { catalogGate.Release(); }
    }));
    SaveCache(catalogCachePath, catalogCache);
    Console.WriteLine($"\r  TMDB {fetched}/{missing.Count} готово   ");

    static string Clean(string? text) => (text ?? string.Empty).Replace('\t', ' ').Replace('\n', ' ').Trim();
    var lines = picks.Select(row =>
    {
        catalogCache.TryGetValue(row.ImdbId, out var e);
        var en = Clean(e?.TitleEn ?? row.Title);
        var ru = Clean(e?.TitleRu);
        var original = Clean(row.OriginalTitle);
        return string.Join('\t',
            row.ImdbId[2..],
            row.Year,
            row.Kind == "movie" ? "m" : "s",
            row.Votes,
            ru == en ? string.Empty : ru,
            en,
            original == en ? string.Empty : original,
            e?.PosterPath ?? string.Empty);
    });
    var catalogPath = Path.Combine(docsDir, "data", "catalog.tsv");
    File.WriteAllText(catalogPath, string.Join('\n', lines), new UTF8Encoding(false));
    Console.WriteLine($"Готово: {catalogPath} ({new FileInfo(catalogPath).Length / 1024:N0} КБ)");
    return;
}

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
var freshCandidates = new HashSet<string>();
bool MaybeFresh(TitleRow r) => r.Votes > FreshMinVotes && r.Rating > FreshMinRating;
foreach (var (kind, quota, firstYear) in kindConfigs)
{
    var picked = new List<TitleRow>();
    foreach (var year in Enumerable.Range(firstYear, currentYear - firstYear + 1).OrderBy(y => y))
    {
        if (!byYear.TryGetValue(kind, out var rows) || !rows.TryGetValue(year, out var list)) continue;
        list.Sort((a, b) => b.Votes.CompareTo(a.Votes));
        picked.AddRange(list.Take(PoolPerYear));
        if (year >= currentYear - 1)
        {
            // Possible new releases past the top-200; their release date
            // (TMDB) decides in pass 2.
            var extra = list.Skip(PoolPerYear).Where(MaybeFresh).ToList();
            picked.AddRange(extra);
            freshCandidates.UnionWith(extra.Select(r => r.ImdbId));
        }
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
    var totalFreshPart = 0;

    foreach (var year in Enumerable.Range(firstYear, currentYear - firstYear + 1).OrderBy(y => y))
    {
        var quotaY = YearQuota(quota, year);
        var votesQuota = (int)Math.Round(quotaY * 0.8);
        var ratingQuota = quotaY - votesQuota;

        var yearRows = candidates[kind].Where(r => r.Year == year && r.Rating >= MinRating).ToList();
        var pool = yearRows.Where(r => !freshCandidates.Contains(r.ImdbId)).ToList(); // votes desc already
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

        bool IsFresh(TitleRow r) =>
            MaybeFresh(r) &&
            cache.TryGetValue(r.ImdbId, out var e) &&
            DateTime.TryParse(e.ReleaseDate, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var released) &&
            released <= DateTime.UtcNow.Date &&
            released >= DateTime.UtcNow.Date.AddDays(-FreshDays);
        var freshPart = yearRows.Except(votesPart).Except(ratingPart)
            .Where(r => IsFresh(r) && HasRuDescription(r))
            .ToList();

        totalVotesPart += votesPart.Count;
        totalRatingPart += ratingPart.Count;
        totalFreshPart += freshPart.Count;
        final.AddRange(votesPart.Concat(ratingPart).Concat(freshPart)
            .OrderBy(ReleaseDateOf)
            .ThenByDescending(r => r.Votes));
    }

    selected[kind] = final;
    Console.WriteLine($"  {kind}: {final.Count:N0} отобрано ({totalVotesPart} по голосам + {totalRatingPart} по рейтингу + " +
                      $"{totalFreshPart} новинок), " +
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

// ---- seasons of the series (daily; long-ended ones monthly) ------------------
// A series whose latest season premiered within a year is shown again in the
// app at that premiere, with the season and the episodes aired so far.

var today = DateTime.UtcNow.Date;
bool SeasonsDue(TmdbEntry e)
{
    if (e.SeasonsCheckedAt is not { } checkedAt) return true;
    var ended = e.TvStatus is "Ended" or "Canceled" &&
                DateTime.TryParse(e.LastAirDate, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var lastAir) &&
                lastAir < today.AddDays(-400);
    return checkedAt < DateTime.UtcNow - (ended ? TimeSpan.FromDays(30) : TimeSpan.FromHours(20));
}
var seasonWork = selected["series"]
    .Where(r => cache.TryGetValue(r.ImdbId, out var e) && e.TmdbId != null && SeasonsDue(e))
    .ToList();
Console.WriteLine($"Сезоны: {seasonWork.Count:N0} к проверке");
var seasonDone = 0;
await Task.WhenAll(seasonWork.Select(async row =>
{
    await gate.WaitAsync();
    try
    {
        await FetchSeasonsAsync(http, cache[row.ImdbId]);
        if (Interlocked.Increment(ref seasonDone) % 500 == 0)
        {
            Console.Write($"\r  сезоны {seasonDone}/{seasonWork.Count}");
            lock (saveLock) { SaveCache(cachePath, cache); }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  ! {row.ImdbId} сезоны: {ex.Message}");
    }
    finally { gate.Release(); }
}));
SaveCache(cachePath, cache);
Console.WriteLine($"\r  сезоны {seasonDone}/{seasonWork.Count} готово   ");

// The season to show: premiered within a year and already on air.
bool CurrentSeason(TmdbEntry? e) =>
    e?.Season is > 0 && e.EpisodesAired is > 0 &&
    DateTime.TryParse(e.SeasonDate, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var premiere) &&
    premiere <= today && premiere > today.AddDays(-365);
Console.WriteLine($"  сезон за год: {selected["series"].Count(r => CurrentSeason(cache.GetValueOrDefault(r.ImdbId)))} " +
                  $"(продолжений: {selected["series"].Count(r => CurrentSeason(cache.GetValueOrDefault(r.ImdbId)) && cache[r.ImdbId].Season > 1)})");

// ---- people: who made the titles (the app's recommendations) ------------------
// Directors and writers of movies rated above 6.5; for series rated above 7.2
// the director, the writer and the creator (showrunner) — each only when the
// series has one person in that role (a crew of many says nothing). Fetched
// once per title and cached.

const double PeopleMovieRating = 6.5;
const double PeopleSeriesRating = 7.2;
var peopleRows = selected["movie"].Where(r => r.Rating > PeopleMovieRating)
    .Concat(selected["series"].Where(r => r.Rating > PeopleSeriesRating))
    .Where(r => cache.TryGetValue(r.ImdbId, out var e) && e.TmdbId != null)
    .ToList();
var peopleWork = peopleRows.Where(r => cache[r.ImdbId].People == null).ToList();
Console.WriteLine($"Создатели: {peopleWork.Count:N0} к загрузке из {peopleRows.Count:N0}");
var peopleDone = 0;
await Task.WhenAll(peopleWork.Select(async row =>
{
    await gate.WaitAsync();
    try
    {
        var entry = cache[row.ImdbId];
        entry.People = await FetchPeopleAsync(http, row.Kind, entry.TmdbId!.Value);
        if (Interlocked.Increment(ref peopleDone) % 500 == 0)
        {
            Console.Write($"\r  создатели {peopleDone}/{peopleWork.Count}");
            lock (saveLock) { SaveCache(cachePath, cache); }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  ! {row.ImdbId} создатели: {ex.Message}");
    }
    finally { gate.Release(); }
}));
SaveCache(cachePath, cache);
Console.WriteLine($"\r  создатели {peopleDone}/{peopleWork.Count} готово   ");

// The link table: a person counts only when they connect two titles or more.
var titlesByPerson = new Dictionary<int, List<string>>();
foreach (var row in peopleRows)
{
    foreach (var person in cache[row.ImdbId].People ?? new List<string>())
    {
        var id = int.Parse(person.Split(':')[1]);
        titlesByPerson.GetOrAdd(id, () => new List<string>()).Add(row.ImdbId);
    }
}
var sharedPeople = titlesByPerson.Where(kv => kv.Value.Distinct().Count() > 1).Select(kv => kv.Key).ToHashSet();
var peopleLinks = new Dictionary<string, List<string>>();
var peopleNames = new Dictionary<string, string>();
foreach (var row in peopleRows)
{
    var credits = (cache[row.ImdbId].People ?? new List<string>())
        .Where(person => sharedPeople.Contains(int.Parse(person.Split(':')[1])))
        .ToList();
    if (credits.Count == 0) continue;
    peopleLinks[row.ImdbId] = credits.Select(person => string.Join(':', person.Split(':').Take(2))).Distinct().ToList();
    foreach (var person in credits)
    {
        var parts = person.Split(':', 3);
        peopleNames[parts[1]] = parts[2];
    }
}
Console.WriteLine($"  связи: {peopleLinks.Count:N0} титлов, {peopleNames.Count:N0} человек");

// ---- in cinemas, not yet online ----------------------------------------------
// A movie is "in cinemas" from its first theatrical release (any country)
// for up to half a year, until a digital release appears anywhere. Checked
// daily for TMDB's "now playing" lists and the recent movie candidates; a
// movie already online, or out for long, is not asked again.

var cinemaCachePath = Path.Combine(cacheDir, "cinema-cache.json");
var cinemaCache = File.Exists(cinemaCachePath)
    ? JsonSerializer.Deserialize<Dictionary<int, CinemaEntry>>(File.ReadAllText(cinemaCachePath)) ?? new()
    : new Dictionary<int, CinemaEntry>();
var cinemaIds = new HashSet<int>();
foreach (var region in new[] { "US", "RU", "GB", "DE", "KZ" })
{
    for (var page = 1; page <= 10; page++)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync(
            $"https://api.themoviedb.org/3/movie/now_playing?api_key={TmdbKey}&region={region}&page={page}"));
        foreach (var movie in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            cinemaIds.Add(movie.GetProperty("id").GetInt32());
        }
        if (page >= doc.RootElement.GetProperty("total_pages").GetInt32()) break;
    }
}
Console.WriteLine($"Кино: {cinemaIds.Count} в прокате по TMDB");
foreach (var row in candidates["movie"].Where(r => r.Year >= currentYear - 1))
{
    if (cache.TryGetValue(row.ImdbId, out var e) && e.TmdbId is { } id) cinemaIds.Add(id);
}
bool CinemaDue(CinemaEntry? c) =>
    c == null ||
    (c.DigitalDate is not { } digital || digital > today) &&
    (c.TheatricalDate is not { } theatrical || theatrical > today.AddDays(-200)) &&
    c.CheckedAt < DateTime.UtcNow.AddHours(-20);
var cinemaWork = cinemaIds.Where(id => CinemaDue(cinemaCache.GetValueOrDefault(id))).ToList();
Console.WriteLine($"  даты выхода: {cinemaWork.Count:N0} к проверке");
await Task.WhenAll(cinemaWork.Select(async id =>
{
    await gate.WaitAsync();
    try
    {
        var entry = await FetchCinemaAsync(http, id);
        lock (cinemaCache) { cinemaCache[id] = entry; }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ! кино {id}: {ex.Message}");
    }
    finally { gate.Release(); }
}));
File.WriteAllText(cinemaCachePath, JsonSerializer.Serialize(cinemaCache));
var inCinema = cinemaCache.Values
    .Where(c => c.ImdbId != null && c.TheatricalDate is { } theatrical &&
                theatrical <= today && theatrical > today.AddDays(-180) &&
                (c.DigitalDate is not { } digital || digital > today))
    .ToList();
var inCinemaIds = inCinema.Select(c => c.ImdbId!).ToHashSet();
Console.WriteLine($"  в кино без цифрового релиза: {inCinema.Count}");

// Kids top: 80% most voted, the rest highest rated of the remaining pool;
// only titles the age rating confirms, with a RU description and a poster.
var kidsSelected = new List<TitleRow>();
foreach (var year in Enumerable.Range(movieFirst, currentYear - movieFirst + 1))
{
    var quotaY = YearQuota(KidsYearQuota, year);
    var votesQuota = (int)Math.Round(quotaY * 0.8);
    var pool = kidsCandidates
        .Where(r => r.Year == year && r.Rating >= MinRating && cache.TryGetValue(r.ImdbId, out var e) &&
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

// ---- creators: who made each title (the app's opened card) -------------------
// For every title of the tops: its director and writer, and for a series its
// creator (showrunner); of several people in one role the one whose best
// known other title has the most votes, named with that title. Candidates
// (the most popular few per role) are cached per title, the people's works
// per person (cache/tmdb-persons.json): only new titles and people are
// asked. creators.json holds person ids per title, names and works once each.

const int CreatorCandidates = 4;
const int PersonRefreshDays = 120;
var creatorRows = selected["movie"].Concat(selected["series"]).Concat(kidsSelected)
    .Where(r => cache.TryGetValue(r.ImdbId, out var e) && e.TmdbId != null)
    .DistinctBy(r => r.ImdbId)
    .ToList();
var crewWork = creatorRows.Where(r => cache[r.ImdbId].Crew == null).ToList();
Console.WriteLine($"Авторы: {crewWork.Count:N0} титлов к загрузке из {creatorRows.Count:N0}");
var crewDone = 0;
await Task.WhenAll(crewWork.Select(async row =>
{
    await gate.WaitAsync();
    try
    {
        var entry = cache[row.ImdbId];
        entry.Crew = await FetchCrewAsync(http, row.Kind, entry.TmdbId!.Value, CreatorCandidates);
        if (Interlocked.Increment(ref crewDone) % 500 == 0)
        {
            Console.Write($"\r  авторы {crewDone}/{crewWork.Count}");
            lock (saveLock) { SaveCache(cachePath, cache); }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  ! {row.ImdbId} авторы: {ex.Message}");
    }
    finally { gate.Release(); }
}));
SaveCache(cachePath, cache);
Console.WriteLine($"\r  авторы {crewDone}/{crewWork.Count} готово   ");

var personsPath = Path.Combine(cacheDir, "tmdb-persons.json");
var persons = File.Exists(personsPath)
    ? JsonSerializer.Deserialize<Dictionary<int, PersonEntry>>(File.ReadAllText(personsPath), cacheJsonOpts) ?? new()
    : new Dictionary<int, PersonEntry>();
var personIds = creatorRows.SelectMany(r => cache[r.ImdbId].Crew ?? new List<string>())
    .Select(c => int.Parse(c.Split(':')[1]))
    .ToHashSet();
var newPersons = personIds.Count(id => !persons.ContainsKey(id));
var personWork = personIds
    .Where(id => !persons.TryGetValue(id, out var p) || p.CheckedAt < DateTime.UtcNow.AddDays(-PersonRefreshDays))
    // New people first, then the longest unchecked; a few thousand a run.
    .OrderBy(id => persons.TryGetValue(id, out var p) ? p.CheckedAt : DateTime.MinValue)
    .Take(Math.Max(3000, newPersons))
    .ToList();
Console.WriteLine($"  люди: {personWork.Count:N0} к загрузке из {personIds.Count:N0}");
var personDone = 0;
var personLock = new object();
void SavePersons()
{
    var tmp = personsPath + ".tmp";
    File.WriteAllText(tmp, JsonSerializer.Serialize(persons, cacheJsonOpts));
    File.Move(tmp, personsPath, overwrite: true);
}
await Task.WhenAll(personWork.Select(async id =>
{
    await gate.WaitAsync();
    try
    {
        var person = await FetchPersonAsync(http, id);
        lock (personLock)
        {
            persons[id] = person;
            if (++personDone % 500 == 0)
            {
                Console.Write($"\r  люди {personDone}/{personWork.Count}");
                SavePersons();
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  ! человек {id}: {ex.Message}");
    }
    finally { gate.Release(); }
}));
SavePersons();
Console.WriteLine($"\r  люди {personDone}/{personWork.Count} готово   ");

// Works: "Тёмный рыцарь|The Dark Knight" (the original title only when it
// differs); names: "Кристофер Нолан|Christopher Nolan" (the same way).
var creatorWorks = new List<string>();
var creatorWorkIndex = new Dictionary<string, int>();
var creatorNames = new Dictionary<string, string>();
var creatorTitles = new Dictionary<string, List<string>>();
static string Pair(string ru, string? other) =>
    string.IsNullOrWhiteSpace(other) || other == ru ? ru : $"{ru}|{other}";
foreach (var row in creatorRows)
{
    var entry = cache[row.ImdbId];
    var media = row.Kind == "series" ? "tv" : "movie";
    var byRole = (entry.Crew ?? new List<string>())
        .Select(c => c.Split(':', 4))
        .GroupBy(p => p[0][0])
        .ToDictionary(g => g.Key, g => g.Select(p => (
            Id: int.Parse(p[1]),
            Popularity: double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture),
            NameEn: p[3])).ToList());
    var chosen = new List<(char Role, int Id, string NameEn, PersonWork? Work)>();
    foreach (var role in new[] { 'c', 'd', 'w' })
    {
        if (!byRole.TryGetValue(role, out var people)) continue;
        var best = people
            .Select(p => (Person: p, Work: persons.GetValueOrDefault(p.Id)?.Works
                .Where(w => w.Department == PersonEntry.Department(role) && !(w.Media == media && w.TmdbId == entry.TmdbId))
                .MaxBy(w => w.Votes)))
            .OrderByDescending(p => p.Work?.Votes ?? -1)
            .ThenByDescending(p => p.Person.Popularity)
            .First();
        chosen.Add((role, best.Person.Id, best.Person.NameEn, best.Work));
    }
    if (chosen.Count == 0) continue;
    var credits = new List<string>();
    foreach (var group in chosen.GroupBy(c => c.Id))
    {
        var first = group.First();
        var nameRu = persons.GetValueOrDefault(first.Id)?.NameRu;
        creatorNames[first.Id.ToString()] = Pair(string.IsNullOrWhiteSpace(nameRu) ? first.NameEn : nameRu, first.NameEn);
        var credit = $"{string.Concat(group.Select(c => c.Role))}:{first.Id}";
        if (group.Select(c => c.Work).FirstOrDefault(w => w != null) is { } bestWork)
        {
            var text = Pair(bestWork.TitleRu, bestWork.TitleOriginal);
            if (!creatorWorkIndex.TryGetValue(text, out var index))
            {
                creatorWorkIndex[text] = index = creatorWorks.Count;
                creatorWorks.Add(text);
            }
            credit += $":{index}";
        }
        credits.Add(credit);
    }
    creatorTitles[row.ImdbId] = credits;
}
// "dw:525:17": roles (c creator, d director, w writer), person id, work index.
File.WriteAllText(Path.Combine(docsDir, "data", "creators.json"),
    JsonSerializer.Serialize(new { names = creatorNames, works = creatorWorks, titles = creatorTitles }, jsonOpts));
Console.WriteLine($"  creators.json: {creatorTitles.Count:N0} титлов, {creatorNames.Count:N0} человек, {creatorWorks.Count:N0} работ");

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
            ["posterSmall"] = tmdb?.PosterPath is null ? null : "https://image.tmdb.org/t/p/w185" + tmdb.PosterPath,
            ["season"] = kind == "series" && CurrentSeason(tmdb) ? tmdb!.Season : null,
            ["seasonDate"] = kind == "series" && CurrentSeason(tmdb) ? tmdb!.SeasonDate : null,
            ["episodesAired"] = kind == "series" && CurrentSeason(tmdb) ? tmdb!.EpisodesAired : null,
            ["episodesTotal"] = kind == "series" && CurrentSeason(tmdb) ? tmdb!.EpisodesTotal : null,
            ["inCinema"] = kind != "series" && inCinemaIds.Contains(row.ImdbId) ? true : null
        });
    }
    var path = Path.Combine(docsDir, "data", fileName);
    File.WriteAllText(path, JsonSerializer.Serialize(items, jsonOpts));
    Console.WriteLine($"  {fileName}: {items.Count:N0} записей");

    // App display shape: only the fields it shows, empty ones left out
    // (a dictionary ignores WhenWritingNull; most titles have no season).
    static Dictionary<string, object?> ToAppShape(Dictionary<string, object?> m) => new Dictionary<string, object?>
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
        ["posterSmall"] = m["posterSmall"],
        ["season"] = m["season"],
        ["seasonDate"] = m["seasonDate"],
        ["episodesAired"] = m["episodesAired"],
        ["episodesTotal"] = m["episodesTotal"],
        ["inCinema"] = m["inCinema"]
    }.Where(field => field.Value is not null).ToDictionary(field => field.Key, field => field.Value);

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

// Who made what: "d:123" (director), "w:…" (writer), "c:…" (creator).
File.WriteAllText(Path.Combine(docsDir, "data", "people.json"),
    JsonSerializer.Serialize(new { names = peopleNames, links = peopleLinks }, jsonOpts));
Console.WriteLine($"  people.json: {peopleLinks.Count} титлов");

// ---- news: what joined the tops, newest first --------------------------------
// The day each title (and each new season, from the second on) first showed
// up in the movie or series top is kept in cache/added-history.json. The
// first run rebuilds it from the published history in git; titles that came
// with an algorithm change are not news, unless they were fresh releases
// (dated by their release then). news.json lists the last half year.

const int NewsDays = 183;
var historyPath = Path.Combine(cacheDir, "added-history.json");
var added = File.Exists(historyPath)
    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(historyPath)) ?? new()
    : RebuildAddedHistory(root);
var todayText = today.ToString("yyyy-MM-dd");
var currentKeys = new List<(string Key, string ImdbId, string Kind, int? Season)>();
foreach (var kind in new[] { "movie", "series" })
{
    foreach (var row in selected[kind])
    {
        var prefix = kind == "movie" ? "m" : "s";
        currentKeys.Add(($"{prefix}:{row.ImdbId}", row.ImdbId, kind, null));
        if (kind == "series" && cache.TryGetValue(row.ImdbId, out var e) && CurrentSeason(e) && e.Season > 1)
        {
            currentKeys.Add(($"s:{row.ImdbId}:S{e.Season}", row.ImdbId, kind, e.Season));
        }
    }
}
var newToday = 0;
foreach (var (key, _, _, _) in currentKeys)
{
    if (added.TryAdd(key, todayText)) newToday++;
}
File.WriteAllText(historyPath, JsonSerializer.Serialize(added));
var newsFrom = today.AddDays(-NewsDays).ToString("yyyy-MM-dd");
var news = currentKeys
    .Where(k => added.TryGetValue(k.Key, out var at) && at != "base" && string.CompareOrdinal(at, newsFrom) >= 0)
    .Select(k => new { imdbId = k.ImdbId, kind = k.Kind, season = k.Season, addedAt = added[k.Key] })
    .OrderByDescending(n => n.addedAt, StringComparer.Ordinal)
    .ToList();
File.WriteAllText(Path.Combine(docsDir, "data", "news.json"), JsonSerializer.Serialize(news, jsonOpts));
Console.WriteLine($"  news.json: {news.Count} записей ({newToday} новых сегодня)");

// Every movie in cinemas, for the app's own lists (not only the tops).
File.WriteAllText(Path.Combine(docsDir, "data", "cinema.json"), JsonSerializer.Serialize(
    inCinema.OrderByDescending(c => c.TheatricalDate).Select(c => new { imdbId = c.ImdbId, title = c.Title }), jsonOpts));
Console.WriteLine($"  cinema.json: {inCinema.Count} записей");

// ---- Russian tops (Kinopoisk) ------------------------------------------------
// The key lives in kp.key (not in git). The first run downloads every Russian
// title with enough votes (a few dozen requests); later runs refresh only the
// current year. Requests go slowly, one at a time.

var kpKeyPath = Path.Combine(root, "kp.key");
var ruCounts = new Dictionary<string, int>();
if (File.Exists(kpKeyPath) && !smoke)
{
    var kpKey = File.ReadAllText(kpKeyPath).Trim();
    var kpDir = Path.Combine(cacheDir, "kp");
    Directory.CreateDirectory(kpDir);
    foreach (var (kind, types, quota, fileName) in new[]
             {
                 ("movie", new[] { "movie", "cartoon" }, RuMovieYearQuota, "ru-movies.json"),
                 ("series", new[] { "tv-series", "animated-series" }, RuSeriesYearQuota, "ru-series.json")
             })
    {
        var kpCachePath = Path.Combine(kpDir, $"{kind}.json");
        var known = File.Exists(kpCachePath)
            ? JsonSerializer.Deserialize<Dictionary<int, KpTitle>>(File.ReadAllText(kpCachePath)) ?? new()
            : new Dictionary<int, KpTitle>();
        var fresh = new List<KpTitle>();
        try
        {
            // Everything once (year ranges small enough for the free tier's
            // 10 pages), then the current year every day.
            var fullMark = Path.Combine(kpDir, $"{kind}.full");
            if (!File.Exists(fullMark))
            {
                await FetchKpRangeAsync(kpKey, types, 1900, currentYear, RuMinVotes, fresh);
                File.WriteAllText(fullMark, DateTime.UtcNow.ToString("o")); // reached only without errors
            }
            else
            {
                await FetchKpRangeAsync(kpKey, types, currentYear, currentYear, RuMinVotesCurrent, fresh);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ! КП {kind}: {ex.Message}");
        }
        // What arrived is kept, also after a failure.
        foreach (var title in fresh)
        {
            known[title.Id] = title;
        }
        File.WriteAllText(kpCachePath, JsonSerializer.Serialize(known));
        Console.WriteLine($"  КП {kind}: {fresh.Count:N0} загружено, {known.Count:N0} в кэше");

        // Per year: 80% most voted, the rest highest rated of the year's
        // most voted; only with a description and a poster, rated 5+.
        var ruSelected = new List<KpTitle>();
        foreach (var yearGroup in known.Values.Where(t => t.Year >= 1900 && t.Year <= currentYear).GroupBy(t => t.Year).OrderBy(g => g.Key))
        {
            var quotaY = YearQuota(quota, yearGroup.Key);
            var votesQuota = (int)Math.Round(quotaY * 0.8);
            var pool = yearGroup
                .Where(t => t.Rating >= MinRating && !string.IsNullOrWhiteSpace(t.Description) && t.Poster != null)
                .OrderByDescending(t => t.Votes)
                .Take(RuPoolPerYear)
                .ToList();
            var votesPart = pool.Take(votesQuota).ToList();
            var ratingPart = pool.Skip(votesQuota).OrderByDescending(t => t.Rating).ThenByDescending(t => t.Votes)
                .Take(quotaY - votesPart.Count).ToList();
            ruSelected.AddRange(votesPart.Concat(ratingPart)
                .OrderBy(t => t.ReleaseDate ?? "9999-99-99")
                .ThenByDescending(t => t.Votes));
        }
        ruCounts[kind] = ruSelected.Count;

        // The app's shape; titles without an IMDb id are keyed "kp<id>".
        var ruItems = ruSelected.Select(t => new Dictionary<string, object?>
        {
            ["imdbId"] = t.ImdbId ?? $"kp{t.Id}",
            ["kpId"] = t.Id,
            ["year"] = t.Year,
            ["releaseDate"] = t.ReleaseDate,
            ["rating"] = Math.Round(t.Rating, 1),
            ["kpRating"] = Math.Round(t.Rating, 1),
            ["votes"] = t.Votes,
            ["titleOriginal"] = t.TitleOriginal,
            ["titleEn"] = t.TitleEn,
            ["titleRu"] = t.TitleRu,
            ["overviewRu"] = t.Description,
            ["posterSmall"] = t.Poster
        }).ToList();
        File.WriteAllText(Path.Combine(docsDir, "data", fileName.Replace(".json", "-slim.json")), JsonSerializer.Serialize(ruItems, jsonOpts));
        File.WriteAllText(Path.Combine(docsDir, "data", fileName.Replace(".json", "-latest.json")),
            JsonSerializer.Serialize(ruItems.Skip(Math.Max(0, ruItems.Count - 50)), jsonOpts));
        Console.WriteLine($"  {fileName.Replace(".json", "-slim.json")}: {ruItems.Count:N0} записей");
    }
}
else
{
    Console.WriteLine("КП: нет kp.key — российские топы не обновляются");
}

// Russian titles of these years: the free tier serves only 10 pages of a
// query, so a range that has more is split in two. Whatever arrived before a
// failure is kept (the caller saves it).
async Task FetchKpRangeAsync(string key, string[] types, int from, int to, int minVotes, List<KpTitle> into)
{
    const int MaxPages = 10;
    var pages = await FetchKpAsync(key, types, from, to, minVotes, into, MaxPages);
    if (pages > MaxPages && from < to)
    {
        var middle = (from + to) / 2;
        // The halves fetch their titles anew; duplicates collapse by id.
        await FetchKpRangeAsync(key, types, from, middle, minVotes, into);
        await FetchKpRangeAsync(key, types, middle + 1, to, minVotes, into);
    }
}

// One query, up to maxPages pages; returns how many pages it has in all.
async Task<int> FetchKpAsync(string key, string[] types, int from, int to, int minVotes, List<KpTitle> result, int maxPages)
{
    var russia = Uri.EscapeDataString("Россия");
    var query = $"limit=250&countries.name={russia}&votes.kp={minVotes}-999999999&sortField=votes.kp&sortType=-1" +
                string.Concat(types.Select(t => $"&type={t}")) +
                (from == to ? $"&year={from}" : $"&year={from}-{to}") +
                string.Concat(new[] { "id", "name", "alternativeName", "enName", "year", "rating", "votes", "externalId",
                    "premiere", "description", "shortDescription", "poster", "type" }.Select(f => $"&selectFields={f}"));
    for (var page = 1; ; page++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.poiskkino.dev/v1.4/movie?{query}&page={page}");
        request.Headers.Add("X-API-KEY", key);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var m in doc.RootElement.GetProperty("docs").EnumerateArray())
        {
            if (KpTitle.From(m) is { } title)
            {
                result.Add(title);
            }
        }

        var pages = doc.RootElement.TryGetProperty("pages", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 1;
        await Task.Delay(TimeSpan.FromSeconds(2)); // slowly
        if (pages > maxPages && from < to)
        {
            // Too big for the free tier: the caller splits the range.
            Console.WriteLine($"  КП {string.Join('/', types)} {from}-{to}: {pages} стр., делю");
            return pages;
        }
        Console.WriteLine($"  КП {string.Join('/', types)} {from}-{to}: стр. {page}/{pages}");
        if (page >= Math.Min(pages, maxPages))
        {
            return pages;
        }
    }
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
    currentYearQuotaSeries = YearQuota(SeriesYearQuota, currentYear),
    ruMovies = ruCounts.GetValueOrDefault("movie"),
    ruSeries = ruCounts.GetValueOrDefault("series")
};
File.WriteAllText(Path.Combine(docsDir, "data", "meta.json"), JsonSerializer.Serialize(meta, jsonOpts));

Console.WriteLine("Готово: docs/data/*.json");

// ---- helpers ---------------------------------------------------------------

async Task DownloadIfNeededAsync(string name, string target, TimeSpan maxAge)
{
    if (File.Exists(target) && File.GetLastWriteTimeUtc(target) > DateTime.UtcNow - maxAge)
    {
        Console.WriteLine($"{name}: кэш свежий");
        return;
    }

    Console.Write($"Качаю {name}… ");
    using var resp = await http.GetAsync($"https://datasets.imdbws.com/{name}", HttpCompletionOption.ResponseHeadersRead);
    resp.EnsureSuccessStatusCode();
    // Into a temp file first: a broken download must not replace the cache.
    var tmp = target + ".tmp";
    await using (var fs = File.Create(tmp))
    {
        await resp.Content.CopyToAsync(fs);
    }
    File.Move(tmp, target, overwrite: true);
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
        // Miniseries (Chernobyl, VisionQuest) are series too.
        else if (f[1] is "tvSeries" or "tvMiniSeries") { kind = "series"; firstYear = seriesFirst; }
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

// The latest season on air: its number and premiere, episodes aired / total.
static async Task FetchSeasonsAsync(HttpClient client, TmdbEntry entry)
{
    using var doc = JsonDocument.Parse(await client.GetStringAsync(
        $"https://api.themoviedb.org/3/tv/{entry.TmdbId}?api_key={TmdbKey}"));
    var root = doc.RootElement;
    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    entry.TvStatus = Text(root, "status");
    entry.LastAirDate = Text(root, "last_air_date");
    entry.Season = null;
    entry.SeasonDate = null;
    entry.EpisodesAired = null;
    entry.EpisodesTotal = null;
    if (root.TryGetProperty("last_episode_to_air", out var last) && last.ValueKind == JsonValueKind.Object &&
        Number(last, "season_number") is > 0 and var season)
    {
        entry.Season = season;
        entry.EpisodesAired = Number(last, "episode_number");
        if (root.TryGetProperty("seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in seasons.EnumerateArray())
            {
                if (Number(s, "season_number") == season)
                {
                    entry.SeasonDate = Text(s, "air_date");
                    entry.EpisodesTotal = Number(s, "episode_count");
                }
            }
        }
    }
    entry.SeasonsCheckedAt = DateTime.UtcNow;
}

// Creator candidates: "role:id:popularity:name", the most popular few per
// role. Movies: directors ('d') and writers ('w'); series also creators
// ('c'). Names in English (TMDB's default).
static async Task<List<string>> FetchCrewAsync(HttpClient client, string kind, int tmdbId, int perRole)
{
    var found = new List<(char Role, int Id, double Popularity, string Name)>();
    void Add(char role, JsonElement person)
    {
        var id = person.GetProperty("id").GetInt32();
        if (found.Any(p => p.Role == role && p.Id == id)) return;
        found.Add((role, id,
            person.TryGetProperty("popularity", out var pop) && pop.ValueKind == JsonValueKind.Number ? pop.GetDouble() : 0,
            person.GetProperty("name").GetString() ?? string.Empty));
    }
    if (kind == "series")
    {
        using var tv = JsonDocument.Parse(await client.GetStringAsync(
            $"https://api.themoviedb.org/3/tv/{tmdbId}?api_key={TmdbKey}&append_to_response=aggregate_credits"));
        foreach (var creator in tv.RootElement.GetProperty("created_by").EnumerateArray()) Add('c', creator);
        foreach (var crew in tv.RootElement.GetProperty("aggregate_credits").GetProperty("crew").EnumerateArray())
        {
            var jobs = crew.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("job").GetString()).ToList();
            if (jobs.Contains("Director")) Add('d', crew);
            if (jobs.Any(j => j is "Writer" or "Screenplay" or "Teleplay")) Add('w', crew);
        }
    }
    else
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync(
            $"https://api.themoviedb.org/3/movie/{tmdbId}/credits?api_key={TmdbKey}"));
        foreach (var crew in doc.RootElement.GetProperty("crew").EnumerateArray())
        {
            var job = crew.GetProperty("job").GetString();
            if (job == "Director") Add('d', crew);
            else if (job is "Screenplay" or "Writer") Add('w', crew);
        }
    }
    return found.GroupBy(p => p.Role)
        .SelectMany(g => g.OrderByDescending(p => p.Popularity).Take(perRole))
        .Select(p => $"{p.Role}:{p.Id}:{p.Popularity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}:{p.Name}")
        .ToList();
}

// A person's Russian name and best known works: per department the three
// with the most votes (a title's own entry is skipped when picking).
static async Task<PersonEntry> FetchPersonAsync(HttpClient client, int id)
{
    using var doc = JsonDocument.Parse(await client.GetStringAsync(
        $"https://api.themoviedb.org/3/person/{id}?api_key={TmdbKey}&language=ru&append_to_response=combined_credits"));
    var root = doc.RootElement;
    var works = new List<PersonWork>();
    foreach (var credit in root.GetProperty("combined_credits").GetProperty("crew").EnumerateArray())
    {
        var department = credit.TryGetProperty("department", out var d) ? d.GetString() : null;
        if (department is not ("Directing" or "Writing" or "Creator")) continue;
        var votes = credit.TryGetProperty("vote_count", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        var title = (credit.TryGetProperty("title", out var t) ? t.GetString() : null)
                    ?? (credit.TryGetProperty("name", out var n) ? n.GetString() : null);
        var original = (credit.TryGetProperty("original_title", out var ot) ? ot.GetString() : null)
                       ?? (credit.TryGetProperty("original_name", out var on) ? on.GetString() : null);
        if (votes <= 0 || string.IsNullOrWhiteSpace(title)) continue;
        works.Add(new PersonWork
        {
            Department = department,
            Media = credit.GetProperty("media_type").GetString() ?? "movie",
            TmdbId = credit.GetProperty("id").GetInt32(),
            Votes = votes,
            TitleRu = title,
            TitleOriginal = original == title ? null : original
        });
    }
    return new PersonEntry
    {
        NameRu = root.TryGetProperty("name", out var name) ? name.GetString() : null,
        CheckedAt = DateTime.UtcNow,
        // Three per department: one may be the title itself.
        Works = works.DistinctBy(w => (w.Department, w.Media, w.TmdbId))
            .GroupBy(w => w.Department)
            .SelectMany(g => g.OrderByDescending(w => w.Votes).Take(3))
            .ToList()
    };
}

// "d:id:name" / "w:id:name" / "c:id:name". Movies: every director and
// writer. Series: the creator, the director and the writer, each only when
// alone in the role.
static async Task<List<string>> FetchPeopleAsync(HttpClient client, string kind, int tmdbId)
{
    var people = new List<string>();
    if (kind == "movie")
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync(
            $"https://api.themoviedb.org/3/movie/{tmdbId}/credits?api_key={TmdbKey}"));
        foreach (var crew in doc.RootElement.GetProperty("crew").EnumerateArray())
        {
            var job = crew.GetProperty("job").GetString();
            var role = job == "Director" ? "d" : job is "Screenplay" or "Writer" ? "w" : null;
            if (role != null)
            {
                people.Add($"{role}:{crew.GetProperty("id").GetInt32()}:{crew.GetProperty("name").GetString()}");
            }
        }
        return people.Distinct().ToList();
    }

    using var tv = JsonDocument.Parse(await client.GetStringAsync(
        $"https://api.themoviedb.org/3/tv/{tmdbId}?api_key={TmdbKey}&append_to_response=aggregate_credits"));
    var root = tv.RootElement;
    var creators = root.GetProperty("created_by").EnumerateArray().ToList();
    if (creators.Count == 1)
    {
        people.Add($"c:{creators[0].GetProperty("id").GetInt32()}:{creators[0].GetProperty("name").GetString()}");
    }
    var directors = new List<JsonElement>();
    var writers = new List<JsonElement>();
    foreach (var crew in root.GetProperty("aggregate_credits").GetProperty("crew").EnumerateArray())
    {
        var jobs = crew.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("job").GetString()).ToList();
        if (jobs.Contains("Director")) directors.Add(crew);
        if (jobs.Any(j => j is "Writer" or "Screenplay" or "Teleplay")) writers.Add(crew);
    }
    if (directors.Count == 1)
    {
        people.Add($"d:{directors[0].GetProperty("id").GetInt32()}:{directors[0].GetProperty("name").GetString()}");
    }
    if (writers.Count == 1)
    {
        people.Add($"w:{writers[0].GetProperty("id").GetInt32()}:{writers[0].GetProperty("name").GetString()}");
    }
    return people;
}

// The added-history from the published data in git: the first commit with a
// title (or season) dates it. A commit after a code change (or the first
// one, or the first with seasons) is an algorithm change: what it brought
// is not news ("base"), except fresh releases, dated by their release.
static Dictionary<string, string> RebuildAddedHistory(string root)
{
    string Git(string arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : string.Empty;
    }

    var history = new Dictionary<string, string>();
    var codeChanged = false;
    var firstData = true;
    var seasonsSeen = false;
    var commits = Git("log --reverse --format=@%H|%ad --date=short --name-only").Split('@', StringSplitOptions.RemoveEmptyEntries);
    foreach (var block in commits)
    {
        var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var head = lines[0].Split('|');
        var (hash, date) = (head[0], DateTime.Parse(head[1], System.Globalization.CultureInfo.InvariantCulture));
        var files = lines.Skip(1).ToList();
        if (files.Contains("Program.cs")) codeChanged = true;
        if (!files.Any(f => f is "docs/data/movies-slim.json" or "docs/data/series-slim.json")) continue;

        var algorithmic = codeChanged || firstData;
        codeChanged = false;
        firstData = false;
        var withSeasons = false;
        foreach (var (file, prefix) in new[] { ("movies-slim.json", "m"), ("series-slim.json", "s") })
        {
            var json = Git($"show {hash}:docs/data/{file}");
            if (json.Length == 0) continue;
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = item.GetProperty("imdbId").GetString()!;
                string? Text(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                string Dated(string? sinceText) =>
                    DateTime.TryParse(sinceText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var since) &&
                    since <= date && since > date.AddDays(-NewsDays)
                        ? since.ToString("yyyy-MM-dd")
                        : "base";
                history.TryAdd($"{prefix}:{id}", algorithmic ? Dated(Text("releaseDate")) : date.ToString("yyyy-MM-dd"));
                if (prefix == "s" && item.TryGetProperty("season", out var season) && season.ValueKind == JsonValueKind.Number && season.GetInt32() > 1)
                {
                    withSeasons = true;
                    history.TryAdd($"s:{id}:S{season.GetInt32()}",
                        algorithmic || !seasonsSeen ? Dated(Text("seasonDate")) : date.ToString("yyyy-MM-dd"));
                }
            }
        }
        seasonsSeen |= withSeasons;
    }
    Console.WriteLine($"  история добавлений восстановлена из git: {history.Count} записей");
    return history;
}

// The first theatrical and the first digital release of a movie, in any country.
static async Task<CinemaEntry> FetchCinemaAsync(HttpClient client, int tmdbId)
{
    using var doc = JsonDocument.Parse(await client.GetStringAsync(
        $"https://api.themoviedb.org/3/movie/{tmdbId}?api_key={TmdbKey}&append_to_response=release_dates,external_ids"));
    var root = doc.RootElement;
    var entry = new CinemaEntry
    {
        ImdbId = root.GetProperty("external_ids").TryGetProperty("imdb_id", out var imdb) && imdb.ValueKind == JsonValueKind.String
            ? imdb.GetString()
            : null,
        Title = root.TryGetProperty("title", out var title) ? title.GetString() : null,
        CheckedAt = DateTime.UtcNow
    };
    foreach (var country in root.GetProperty("release_dates").GetProperty("results").EnumerateArray())
    {
        foreach (var release in country.GetProperty("release_dates").EnumerateArray())
        {
            if (!DateTime.TryParse(release.GetProperty("release_date").GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var date))
            {
                continue;
            }
            date = date.Date;
            switch (release.GetProperty("type").GetInt32())
            {
                case 2 or 3:
                    if (entry.TheatricalDate is not { } t || date < t) entry.TheatricalDate = date;
                    break;
                case 4:
                    if (entry.DigitalDate is not { } d || date < d) entry.DigitalDate = date;
                    break;
            }
        }
    }
    return entry;
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

/// <summary>A Russian title from the Kinopoisk API, as cached.</summary>
internal sealed record KpTitle(int Id, int Year, string TitleRu, string? TitleEn, string TitleOriginal,
    double Rating, int Votes, string? ImdbId, string? ReleaseDate, string? Description, string? Poster)
{
    public static KpTitle? From(JsonElement m)
    {
        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        static double Num(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble() : 0;
        static JsonElement Obj(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v : default;

        var name = Str(m, "name");
        var year = (int)Num(m, "year");
        if (string.IsNullOrWhiteSpace(name) || year == 0)
        {
            return null;
        }

        var premiere = Obj(m, "premiere");
        var date = Str(premiere, "russia") ?? Str(premiere, "world");
        var imdb = Str(Obj(m, "externalId"), "imdb");
        var description = Str(m, "description");
        if (string.IsNullOrWhiteSpace(description))
        {
            description = Str(m, "shortDescription");
        }

        return new KpTitle(
            (int)Num(m, "id"), year, name,
            Str(m, "enName"),
            Str(m, "alternativeName") is { Length: > 0 } alt ? alt : Str(m, "enName") ?? name,
            Num(Obj(m, "rating"), "kp"), (int)Num(Obj(m, "votes"), "kp"),
            imdb is { Length: > 2 } && imdb.StartsWith("tt") ? imdb : null,
            date is { Length: >= 10 } ? date[..10] : null,
            description?.Trim(),
            Str(Obj(m, "poster"), "previewUrl"));
    }
}

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
    // Directors, writers, creators: "d:id:name" (FetchPeopleAsync); null = not fetched.
    public List<string>? People { get; set; }
    // Creator candidates: "role:id:popularity:name" (FetchCrewAsync); null = not fetched.
    public List<string>? Crew { get; set; }
    // Series: the latest season on air (FetchSeasonsAsync).
    public DateTime? SeasonsCheckedAt { get; set; }
    public string? TvStatus { get; set; }
    public string? LastAirDate { get; set; }
    public int? Season { get; set; }
    public string? SeasonDate { get; set; }
    public int? EpisodesAired { get; set; }
    public int? EpisodesTotal { get; set; }
}

internal sealed class PersonEntry
{
    public string? NameRu { get; set; }
    public DateTime CheckedAt { get; set; }
    public List<PersonWork> Works { get; set; } = new();

    // The department whose works name a person in that role.
    public static string Department(char role) => role switch
    {
        'd' => "Directing",
        'w' => "Writing",
        _ => "Creator"
    };
}

internal sealed class PersonWork
{
    public string Department { get; set; } = string.Empty;
    public string Media { get; set; } = string.Empty;
    public int TmdbId { get; set; }
    public int Votes { get; set; }
    public string TitleRu { get; set; } = string.Empty;
    public string? TitleOriginal { get; set; }
}

internal sealed class CinemaEntry
{
    public string? ImdbId { get; set; }
    public string? Title { get; set; }
    public DateTime? TheatricalDate { get; set; }
    public DateTime? DigitalDate { get; set; }
    public DateTime CheckedAt { get; set; }
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
