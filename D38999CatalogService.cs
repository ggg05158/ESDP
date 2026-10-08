using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ESDP
{
    public sealed class D38999WireRequirement
    {
        public int Gauge { get; init; }
        public int? MinimumCircuits { get; init; }
        public int? MaximumCircuits { get; init; }
    }

    public sealed class D38999SearchCriteria
    {
        public List<int> AcceptableGauges { get; } = new();
        public int? MinimumCircuits { get; init; }
        public int? MaximumCircuits { get; init; }
        public string ConnectorType { get; init; } = "";
    }

    public sealed class D38999ParsedPartNumber
    {
        public required D38999Configuration Configuration { get; init; }
        public string ClassCode { get; init; } = "";
        public string ContactStyleCode { get; init; } = "";
        public string KeyingCode { get; init; } = "";
    }

    public sealed class D38999MatingResult
    {
        public bool IsCompatible { get; init; }
        public bool IsConclusive { get; init; } = true;
        public List<string> Reasons { get; init; } = new();
    }

    public sealed class D38999SearchResult
    {
        public required D38999InsertArrangement Arrangement { get; init; }
        public required IReadOnlyList<D38999Configuration> Configurations { get; init; }
        public int PrimaryCompatibleContacts { get; init; }
        public int RequiredMinimum { get; init; }
        public int SpareContacts { get; init; }
        public string CompatibleSummary { get; init; } = "";
        public string PatternPreview => Configurations.FirstOrDefault()?.PartNumberPattern ?? "";
    }

    public sealed class D38999Configuration
    {
        public string PartNumberPattern { get; init; } = "";
        public string Slash { get; init; } = "";
        public string ConnectorType { get; init; } = "";
        public int ShellSize { get; init; }
        public string Insert { get; init; } = "";
        public string ArrangementId { get; init; } = "";
        public string AllowedClasses { get; init; } = "";
        public string AllowedContactStyles { get; init; } = "";
        public string AllowedKeying { get; init; } = "";
    }

    public sealed class D38999InsertArrangement
    {
        public string ArrangementId { get; init; } = "";
        public int ShellSize { get; init; }
        public string Insert { get; init; } = "";
        public int TotalContacts { get; init; }
        public Dictionary<string, int> ContactCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string ServiceRating { get; init; } = "";
        public string Category { get; init; } = "";
        public string SpecialContactNote { get; init; } = "";
        public string FirewallAllowed { get; init; } = "";
        public string PlannedImageFile { get; init; } = "";
        public string ImageStatus { get; init; } = "";
        public string VisualSourceArrangement { get; init; } = "";
    }

    public sealed class D38999ContactRule
    {
        public string RuleId { get; init; } = "";
        public string InsertContactSize { get; init; } = "";
        public string ContactCategory { get; init; } = "";
        public HashSet<int> AllowedAwg { get; } = new();
        public string NewDesignStatus { get; init; } = "";
        public string CableSpec { get; init; } = "";
    }

    public sealed class D38999CatalogService
    {
        private const string ImageResourceRoot =
            "Resources/D38999_connector_data/D38999_Insert_Images/";

        private static readonly Lazy<D38999CatalogService> LazyInstance =
            new(() => new D38999CatalogService());

        public static D38999CatalogService Instance => LazyInstance.Value;

        public IReadOnlyList<D38999InsertArrangement> Arrangements { get; }
        public IReadOnlyList<D38999Configuration> Configurations { get; }
        public IReadOnlyList<D38999ContactRule> ContactRules { get; }
        public IReadOnlyList<string> ConnectorTypes { get; }
        public IReadOnlyList<string> ServiceRatings { get; }
        public IReadOnlyList<string> Categories { get; }
        public IReadOnlyList<int> AvailableGauges { get; }

        private D38999CatalogService()
        {
            var catalog = D38999CatalogData.Load();
            Arrangements = catalog.Arrangements;
            Configurations = catalog.Configurations;
            ContactRules = catalog.ContactRules;

            ConnectorTypes = Configurations.Select(item => item.ConnectorType)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            ServiceRatings = Arrangements.Select(item => item.ServiceRating)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            Categories = Arrangements.Select(item => item.Category)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            AvailableGauges = ContactRules
                .Where(rule => !rule.NewDesignStatus.Equals(
                    "INACTIVE_FOR_NEW_DESIGN", StringComparison.OrdinalIgnoreCase))
                .SelectMany(rule => rule.AllowedAwg)
                .Distinct()
                .OrderByDescending(value => value)
                .ToList();
        }

        public IReadOnlyList<D38999SearchResult> Search(D38999SearchCriteria criteria)
        {
            IEnumerable<D38999InsertArrangement> source = Arrangements;
            var configurationLookup = Configurations
                .GroupBy(item => item.ArrangementId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);
            var gauges = criteria.AcceptableGauges.Distinct().ToList();
            var results = new List<D38999SearchResult>();

            foreach (var arrangement in source)
            {
                if (!configurationLookup.TryGetValue(arrangement.ArrangementId,
                        out var configurations))
                    configurations = new List<D38999Configuration>();

                if (!string.IsNullOrWhiteSpace(criteria.ConnectorType))
                {
                    configurations = configurations.Where(item => item.ConnectorType.Equals(
                        criteria.ConnectorType, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (configurations.Count == 0) continue;
                }

                var compatibleCounts = gauges
                    .Select(gauge => new
                    {
                        Gauge = gauge,
                        Count = GetCompatibleContactCount(arrangement, gauge)
                    })
                    .ToList();
                bool anyGaugeMatches = gauges.Count == 0 || compatibleCounts.Any(pair =>
                    pair.Count > 0 &&
                    (!criteria.MinimumCircuits.HasValue ||
                     pair.Count >= criteria.MinimumCircuits.Value) &&
                    (!criteria.MaximumCircuits.HasValue ||
                     pair.Count <= criteria.MaximumCircuits.Value));
                if (!anyGaugeMatches) continue;

                int requiredMinimum = criteria.MinimumCircuits ?? 0;
                int primaryCompatible = compatibleCounts.Count == 0
                    ? arrangement.TotalContacts
                    : compatibleCounts.Max(pair => pair.Count);
                string summary = compatibleCounts.Count == 0
                    ? "게이지 조건 없음"
                    : string.Join(" 또는 ", compatibleCounts.Select(pair =>
                        $"AWG {pair.Gauge}: {pair.Count}개"));

                results.Add(new D38999SearchResult
                {
                    Arrangement = arrangement,
                    Configurations = configurations,
                    PrimaryCompatibleContacts = primaryCompatible,
                    RequiredMinimum = requiredMinimum,
                    SpareContacts = Math.Max(0, primaryCompatible - requiredMinimum),
                    CompatibleSummary = summary
                });
            }

            return results
                .OrderBy(item => item.SpareContacts)
                .ThenBy(item => item.Arrangement.ShellSize)
                .ThenBy(item => item.Arrangement.TotalContacts)
                .ThenBy(item => item.Arrangement.ArrangementId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public BitmapImage? LoadInsertImage(D38999InsertArrangement arrangement)
        {
            try
            {
                string file = arrangement.PlannedImageFile;
                var resource = OpenImageResource(file);
                if (resource == null &&
                    !string.IsNullOrWhiteSpace(arrangement.VisualSourceArrangement))
                {
                    var fallback = Arrangements.FirstOrDefault(item => item.ArrangementId.Equals(
                        arrangement.VisualSourceArrangement, StringComparison.OrdinalIgnoreCase));
                    if (fallback != null)
                        resource = OpenImageResource(fallback.PlannedImageFile);
                }
                if (resource == null) return null;

                using (resource.Stream)
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = resource.Stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch
            {
                // 핀 배열 이미지는 선택 기능의 보조 자료다. 리소스가 없거나
                // 손상되어도 검색과 품번 적용은 계속 사용할 수 있어야 한다.
                return null;
            }
        }

        public static string SuggestedConnectorName(D38999SearchResult result)
        {
            var configuration = result.Configurations.FirstOrDefault();
            if (configuration == null) return $"D38999 {result.Arrangement.ArrangementId}";
            return BuildPartNumber(
                configuration,
                PreferredCode(configuration.AllowedClasses, "W"),
                PreferredCode(configuration.AllowedContactStyles, "P"),
                PreferredCode(configuration.AllowedKeying, "N"));
        }

        public static string BuildPartNumber(
            D38999Configuration configuration,
            string classCode,
            string contactStyleCode,
            string keyingCode)
        {
            string fullPartNumber = configuration.PartNumberPattern
                .Replace("{Class}", classCode, StringComparison.OrdinalIgnoreCase)
                .Replace("{Contact}", contactStyleCode, StringComparison.OrdinalIgnoreCase)
                .Replace("{Keying}", keyingCode, StringComparison.OrdinalIgnoreCase);
            const string displayPrefix = "D38999/";
            return fullPartNumber.StartsWith(displayPrefix, StringComparison.OrdinalIgnoreCase)
                ? fullPartNumber[displayPrefix.Length..]
                : fullPartNumber;
        }

        public bool TryParsePartNumber(
            string? text, out D38999ParsedPartNumber? parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string candidate = text.Trim();
            string fullCandidate = candidate.StartsWith(
                "D38999/", StringComparison.OrdinalIgnoreCase)
                ? candidate
                : $"D38999/{candidate}";
            foreach (var configuration in Configurations)
            {
                string expression = Regex.Escape(configuration.PartNumberPattern)
                    .Replace(Regex.Escape("{Class}"), "(?<class>[A-Za-z0-9]+)")
                    .Replace(Regex.Escape("{Contact}"), "(?<contact>[A-Za-z0-9]+)")
                    .Replace(Regex.Escape("{Keying}"), "(?<keying>[A-Za-z0-9]+)");
                var match = Regex.Match(fullCandidate, $"^{expression}$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                string classCode = match.Groups["class"].Value;
                string contactCode = match.Groups["contact"].Value;
                string keyingCode = match.Groups["keying"].Value;
                if (!CodeAllowed(configuration.AllowedClasses, classCode) ||
                    !CodeAllowed(configuration.AllowedContactStyles, contactCode) ||
                    !CodeAllowed(configuration.AllowedKeying, keyingCode))
                    continue;
                parsed = new D38999ParsedPartNumber
                {
                    Configuration = configuration,
                    ClassCode = classCode,
                    ContactStyleCode = contactCode,
                    KeyingCode = keyingCode
                };
                return true;
            }
            return false;
        }

        public D38999MatingResult AnalyzeMating(
            D38999ParsedPartNumber first, D38999ParsedPartNumber second)
        {
            var reasons = new List<string>();
            if (!first.Configuration.ArrangementId.Equals(
                    second.Configuration.ArrangementId, StringComparison.OrdinalIgnoreCase))
                reasons.Add($"인서트 배열 불일치 ({first.Configuration.ArrangementId} / {second.Configuration.ArrangementId})");

            string firstFamily = SeriesFamily(first.Configuration.Slash);
            string secondFamily = SeriesFamily(second.Configuration.Slash);
            if (!string.IsNullOrWhiteSpace(firstFamily) &&
                !string.IsNullOrWhiteSpace(secondFamily) && firstFamily != secondFamily)
                reasons.Add($"D38999 시리즈 불일치 ({firstFamily} / {secondFamily})");

            bool firstPlug = first.Configuration.ConnectorType.Contains(
                "plug", StringComparison.OrdinalIgnoreCase);
            bool secondPlug = second.Configuration.ConnectorType.Contains(
                "plug", StringComparison.OrdinalIgnoreCase);
            bool firstReceptacle = first.Configuration.ConnectorType.Contains(
                "receptacle", StringComparison.OrdinalIgnoreCase);
            bool secondReceptacle = second.Configuration.ConnectorType.Contains(
                "receptacle", StringComparison.OrdinalIgnoreCase);
            bool rolesKnown = (firstPlug || firstReceptacle) && (secondPlug || secondReceptacle);
            if (rolesKnown && firstPlug == secondPlug)
                reasons.Add($"결합 형상 불일치 ({first.Configuration.ConnectorType} / {second.Configuration.ConnectorType})");

            if (!AreComplementaryContacts(first.ContactStyleCode, second.ContactStyleCode))
                reasons.Add($"접점 형식 불일치 ({first.ContactStyleCode} / {second.ContactStyleCode})");
            if (!first.KeyingCode.Equals(second.KeyingCode, StringComparison.OrdinalIgnoreCase))
                reasons.Add($"키잉 불일치 ({first.KeyingCode} / {second.KeyingCode})");

            return new D38999MatingResult
            {
                IsCompatible = reasons.Count == 0,
                IsConclusive = rolesKnown,
                Reasons = reasons
            };
        }

        private static bool AreComplementaryContacts(string first, string second)
        {
            var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["P"] = "S", ["S"] = "P", ["A"] = "B", ["B"] = "A",
                ["X"] = "Z", ["Z"] = "X", ["C"] = "D", ["D"] = "C",
                ["R"] = "M", ["M"] = "R", ["G"] = "U", ["U"] = "G"
            };
            return pairs.TryGetValue(first, out string? expected) &&
                   expected.Equals(second, StringComparison.OrdinalIgnoreCase);
        }

        private static string SeriesFamily(string slash)
            => slash.StartsWith("/2", StringComparison.Ordinal) ? "Series III" :
               slash.StartsWith("/4", StringComparison.Ordinal) ? "Series IV" : "";

        public static IReadOnlyList<string> SplitCodes(string value)
            => value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static bool CodeAllowed(string values, string candidate)
            => SplitCodes(values).Contains(candidate, StringComparer.OrdinalIgnoreCase);

        private static string PreferredCode(string values, string preferred)
        {
            var options = SplitCodes(values);
            return options.FirstOrDefault(value => value.Equals(
                       preferred, StringComparison.OrdinalIgnoreCase))
                   ?? options.FirstOrDefault()
                   ?? "";
        }

        public static string? ExtractArrangementId(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var match = Regex.Match(text, @"(?<!\d)(\d{1,2}-\d{1,2})(?!\d)");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static System.Windows.Resources.StreamResourceInfo? OpenImageResource(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            string normalized = file.Replace('\\', '/').TrimStart('/');
            try
            {
                string assemblyName = typeof(D38999CatalogService).Assembly.GetName().Name
                                      ?? "ESDP";
                return Application.GetResourceStream(
                    new Uri($"/{assemblyName};component/{ImageResourceRoot}{normalized}",
                        UriKind.Relative));
            }
            catch
            {
                return null;
            }
        }

        private int GetCompatibleContactCount(D38999InsertArrangement arrangement, int gauge)
        {
            var compatibleSizes = ContactRules
                .Where(rule => !rule.NewDesignStatus.Equals(
                    "INACTIVE_FOR_NEW_DESIGN", StringComparison.OrdinalIgnoreCase))
                .Where(rule => rule.AllowedAwg.Contains(gauge))
                .Select(rule => rule.InsertContactSize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return arrangement.ContactCounts
                .Where(pair => compatibleSizes.Contains(pair.Key))
                .Sum(pair => pair.Value);
        }

        private bool CanAllocateMinimums(
            D38999InsertArrangement arrangement,
            IReadOnlyList<D38999WireRequirement> requirements)
        {
            var active = requirements
                .Where(item => (item.MinimumCircuits ?? 0) > 0)
                .ToList();
            if (active.Count <= 1) return true;

            var sizes = arrangement.ContactCounts.Where(pair => pair.Value > 0).ToList();
            int source = 0;
            int sizeStart = 1;
            int requirementStart = sizeStart + sizes.Count;
            int sink = requirementStart + active.Count;
            var flow = new IntMaxFlow(sink + 1);
            for (int sizeIndex = 0; sizeIndex < sizes.Count; sizeIndex++)
            {
                flow.AddEdge(source, sizeStart + sizeIndex, sizes[sizeIndex].Value);
                for (int requirementIndex = 0; requirementIndex < active.Count; requirementIndex++)
                {
                    if (IsContactSizeCompatible(sizes[sizeIndex].Key,
                            active[requirementIndex].Gauge))
                        flow.AddEdge(sizeStart + sizeIndex,
                            requirementStart + requirementIndex,
                            sizes[sizeIndex].Value);
                }
            }
            int totalRequired = 0;
            for (int index = 0; index < active.Count; index++)
            {
                int minimum = active[index].MinimumCircuits ?? 0;
                totalRequired += minimum;
                flow.AddEdge(requirementStart + index, sink, minimum);
            }
            return flow.Compute(source, sink) == totalRequired;
        }

        private bool IsContactSizeCompatible(string contactSize, int gauge)
            => ContactRules.Any(rule =>
                !rule.NewDesignStatus.Equals(
                    "INACTIVE_FOR_NEW_DESIGN", StringComparison.OrdinalIgnoreCase) &&
                rule.InsertContactSize.Equals(contactSize, StringComparison.OrdinalIgnoreCase) &&
                rule.AllowedAwg.Contains(gauge));

        private sealed class IntMaxFlow
        {
            private sealed class Edge
            {
                public int To;
                public int Reverse;
                public int Capacity;
            }

            private readonly List<Edge>[] _graph;

            public IntMaxFlow(int nodeCount)
                => _graph = Enumerable.Range(0, nodeCount)
                    .Select(_ => new List<Edge>()).ToArray();

            public void AddEdge(int from, int to, int capacity)
            {
                var forward = new Edge { To = to, Reverse = _graph[to].Count, Capacity = capacity };
                var reverse = new Edge { To = from, Reverse = _graph[from].Count, Capacity = 0 };
                _graph[from].Add(forward);
                _graph[to].Add(reverse);
            }

            public int Compute(int source, int sink)
            {
                int total = 0;
                while (true)
                {
                    var previousNode = Enumerable.Repeat(-1, _graph.Length).ToArray();
                    var previousEdge = new int[_graph.Length];
                    var queue = new Queue<int>();
                    queue.Enqueue(source);
                    previousNode[source] = source;
                    while (queue.Count > 0 && previousNode[sink] < 0)
                    {
                        int node = queue.Dequeue();
                        for (int index = 0; index < _graph[node].Count; index++)
                        {
                            var edge = _graph[node][index];
                            if (edge.Capacity <= 0 || previousNode[edge.To] >= 0) continue;
                            previousNode[edge.To] = node;
                            previousEdge[edge.To] = index;
                            queue.Enqueue(edge.To);
                        }
                    }
                    if (previousNode[sink] < 0) return total;
                    int amount = int.MaxValue;
                    for (int node = sink; node != source; node = previousNode[node])
                        amount = Math.Min(amount,
                            _graph[previousNode[node]][previousEdge[node]].Capacity);
                    for (int node = sink; node != source; node = previousNode[node])
                    {
                        var edge = _graph[previousNode[node]][previousEdge[node]];
                        edge.Capacity -= amount;
                        _graph[node][edge.Reverse].Capacity += amount;
                    }
                    total += amount;
                }
            }
        }
    }
}
