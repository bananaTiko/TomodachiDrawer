using System.Diagnostics;
using Google.OrTools.ConstraintSolver;
using SkiaSharp;
using TomodachiDrawer.Core.ImageProcessing;
using TomodachiDrawer.Core.ImageProcessing.Denoising;
using TomodachiDrawer.Core.ImageProcessing.Quantizers;
using TomodachiDrawer.Core.Interfaces;
using TomodachiDrawer.Core.Models;
using TomodachiDrawer.Core.OutputSinks;

namespace TomodachiDrawer.Core
{
    public class CanvasDrawer
    {
        public const int CanvasWidth = 256;
        public const int CanvasHeight = 256;

        private int _cursorX = 0;
        private int _cursorY = 0;

        private readonly ISwitchOutput _realOutput;
        private readonly ColourPalette _palette;
        private readonly CanvasToolbar _toolbar;
        private readonly Action<string> _log;
        private readonly SwitchVersion _switchVersion;

        public CanvasDrawer(
            ISwitchOutput outputSink,
            SwitchVersion switchVersion,
            Action<string>? logger = null
        )
        {
            _realOutput = outputSink;
            _palette = new(outputSink);
            _toolbar = new(outputSink);
            _log = logger ?? Console.WriteLine;

            if (switchVersion == SwitchVersion.None)
                throw new ArgumentOutOfRangeException(
                    nameof(switchVersion),
                    "Switch version must be set."
                );

            _switchVersion = switchVersion;
        }

        public static float GetRecommendedTSPSolveTime(int width, int height)
        {
            const int squared64 = 64 * 64;
            const int squared128 = 128 * 128;
            const int squared192 = 192 * 192;
            const int squared256 = 256 * 256;

            int pixels = width * height;
            if (pixels <= squared64)
                return 0.5f;
            else if (pixels <= squared128)
                return 1.5f;
            else if (pixels <= squared192)
                return 2.75f;
            else if (pixels <= squared256)
                return 4.0f;
            else
            {
                return 5.0f; // should ever reach here...
            }
        }

        public async Task DrawImage(SKBitmap image, DrawImageSettings settings)
        {
            if (image.Width > CanvasWidth || image.Height > CanvasHeight)
                throw new InvalidDataException(
                    $"Image too big. Max is {CanvasWidth}x{CanvasHeight}."
                );
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
                settings.TSPTimeLimit,
                0.0f,
                nameof(settings.TSPTimeLimit)
            );

            // TODO: Basic -> Pro mode auto-switch is intentionally commented out for now.
            // This input sequence has not been verified across Switch versions/game states, and
            // a bad mode-toggle sequence before drawing would desync every later cursor input.
            // If this is validated, uncomment the call and helper below so exports can attempt
            // to put basic-mode users into pro mode before any drawing begins.
            //
            // _log("Attempting to switch drawing UI from Basic mode to Pro mode...");
            // SwitchBasicModeToProMode();

            // Stages:
            // 1: Perform Color quantization to the tomodachi life pallete
            // 2: Split colors into distinct ColorLayers/passes (undecided on which)
            // 3: Find uniform areas of the same color for each brush size, ensuring to avoid ones already covered by a larger stamp.
            // 4: Fine detail pass for everything else, effectively subtracting from the colorpasses the stamps and then filling in those remaining
            // pixels. TSP-like optimization should be done here (alternative to snaking)

            // Other things: Color Pass Order Optimizations for both stamp pass and fine detail pass to minimize
            // travel distance. Need to look at the ai-slop version to try and figure out how that works there.

            // Also we need to pass in the quantization method and dithering settings as arguments.

            if (!string.IsNullOrEmpty(settings.DenoiserName))
            {
                image = ImageDenoiser.DenoiseImage(image, settings.DenoiserName);
            }

            // Quantized Map is a 2D array of PaletteColours.
            var quantizedMap = _palette.QuantizeImage(image, settings.QuantizerSettings);

            // First off we are just putting all the individual details into the fine detail pass,
            // following passes will start to remove from that and add to the stamp passes.
            // TODO: This doesnt really make too much sense to be in the palette class... Maybe move here?
            var layers = _palette.BuildFineLayers(quantizedMap);

            // If the image is 256x256 and has no transparent pixels at all we can use the bucket tool
            // for the most prevelant colour to save time.
            // This is done before the large brush detection to avoid needing to run stuff to count the large brush stuff to find the
            // biggest.
            PaletteColour? bucketColour = null;

            // This below uses the bucket on switch 2, but is generally stable enough since it is just a one off bucket use
            // that doesnt even move the cursor.
            // The dynamic bucket fill on the other hand is prone to desyncs even on switch 2, thus is classified as experimental.
            if (
                _switchVersion == SwitchVersion.Switch2
                && image.Width == 256
                && image.Height == 256
            )
            {
                _log("Seeing if we can use the bucket to save time");
                bool anyTransparent = false;
                for (int x = 0; x < image.Width; x++)
                {
                    for (int y = 0; y < image.Height; y++)
                    {
                        if (image.GetPixel(x, y).Alpha < 128)
                        {
                            anyTransparent = true;
                            break;
                        }
                    }
                    if (anyTransparent)
                        break;
                }

                if (!anyTransparent)
                {
                    bucketColour = layers.MaxBy(l => l.FineDetailPoints.Count)!.Colour;
                    // We need to then remove it from the rest of the drawing so it doesnt draw it now.
                    layers.RemoveAll(l => l.Colour == bucketColour); // is only one but this is easiest.
                    _log(
                        $"\tUsing bucket to fill most prevalent colour: {bucketColour.Value.DisplayName}"
                    );
                    _toolbar.SelectBucket();
                    _palette.SelectColour(bucketColour.Value, 25.0);
                    _realOutput.Tap(Button.A);
                    _realOutput.Delay(1000); // This is probably generous but bucket fill seems to cause a short stutter.
                }
                else
                {
                    _log("\tCan't. Image has transparency.");
                }
            }

            if (settings.EnableExperimentalFeatures)
            {
                if (bucketColour is null || IsBlankCanvasColour(bucketColour.Value))
                {
                    int skippedWhiteRegions = RemoveEnclosedBlankCanvasRegions(
                        layers,
                        image.Width,
                        image.Height
                    );
                    if (skippedWhiteRegions > 0)
                        _log(
                            $"\tSkipped {skippedWhiteRegions} enclosed white canvas regions that are already bounded by other colours."
                        );
                }

                if (_switchVersion == SwitchVersion.Switch2)
                {
                    _log("Finding large bucket-fillable zones...");
                    int sum = 0;
                    foreach (var l in layers)
                    {
                        sum += DetectBucketZones(l, image.Width, image.Height);
                    }
                    _log($"\tFound {sum} dynamic bucket fill zones across image.");
                }
                else
                {
                    _log("Can't perform large bucket-fillable search because Switch 1 is laggy :(");
                }
            }
            else
            {
                _log("Experimental features disabled, so not running dynamic bucket fill scan.");
            }

            // Stamp/uniform area detection
            // TODO: This not useful with the new bucket-fillable search..? Except unless theres
            // a large number of small areas that were rejected for being too small during the bucket zone search.
            // TODO: Figure that out lol
            _log("Detecting uniform areas for large brushes...");
            if (!settings.DisableLargeBrush)
            {
                foreach (var l in layers)
                {
                    DetectUniformAreas(l, image.Width, image.Height);
                }
            }

            double totalInLayerTime = 0.0;

            var totalLayers = layers.Count;
            // 80% divided by total layers.
            int layerNumber = 0;
            foreach (var l in layers)
            {
                layerNumber++;

                _palette.SelectColour(l.Colour, 25.0);

                // STAMPS
                if (l.StampsBySize?.Count > 0)
                {
                    var stampSink = new TimingSink();
                    foreach (var sbs in l.StampsBySize)
                    {
                        if (sbs.Value.Count == 0)
                            continue;
                        int brushSize = sbs.Key;

                        _toolbar.SelectBrush(stampSink, brushSize);

                        var dumbRoute = new List<CanvasPoint>(sbs.Value);
                        var pointCount = dumbRoute.Count;
                        float tspTime = 0.5f;
                        if (pointCount > 200)
                            tspTime = 1.5f;
                        else if (pointCount > 100)
                            tspTime = 1.0f;
                        var optimizedRoute = PerformTSP(dumbRoute, tspTime); // half a sec per stamp size per colour is prob reasonable?
                        optimizedRoute ??= dumbRoute;

                        foreach (var point in optimizedRoute)
                        {
                            NavigateTo(stampSink, point);
                            (stampSink as ISwitchOutput).Tap(Button.A);
                        }
                    }
                    _log($"\tStamps: {stampSink.TotalSeconds:F3}s");
                    stampSink.ReplayTo(_realOutput);
                    totalInLayerTime += stampSink.TotalTime.TotalSeconds;
                }
                // END STAMPS.

                // ============= Fine details
                if (l.FineDetailPoints.Count > 0)
                {
                    _toolbar.SelectBrush(1); // no-op if already selected.

                    // Dry run both to get timing, TimingSink stores
                    // the outputs and time taken so it can be replayed without needing to rerun
                    // the tsp solve or snake logic (snake logic is compartively short but it also replays)
                    int savedX = _cursorX;
                    int savedY = _cursorY;

                    var snakeSink = new TimingSink();
                    FineDetailSnake(snakeSink, l);

                    int afterSnakeX = _cursorX;
                    int afterSnakeY = _cursorY;

                    _cursorX = savedX;
                    _cursorY = savedY;

                    var tspSink = new TimingSink();
                    FineDetailTsp(tspSink, l, settings.TSPTimeLimit);

                    int afterTspX = _cursorX;
                    int afterTspY = _cursorY;

                    //_cursorX = savedX;
                    //_cursorY = savedY;

                    bool tspHasSolution = tspSink.TotalMilliseconds > 0;
                    bool usedSnake =
                        !tspHasSolution || snakeSink.TotalMilliseconds <= tspSink.TotalMilliseconds;
                    if (usedSnake)
                    {
                        snakeSink.ReplayTo(_realOutput);
                        totalInLayerTime += snakeSink.TotalTime.TotalSeconds;
                        _cursorX = afterSnakeX;
                        _cursorY = afterSnakeY;
                    }
                    else
                    {
                        tspSink.ReplayTo(_realOutput);
                        totalInLayerTime += tspSink.TotalTime.TotalSeconds;
                        _cursorX = afterTspX;
                        _cursorY = afterTspY;
                    }
                    string tspPart = tspHasSolution
                        ? $"{tspSink.TotalTime.TotalSeconds:F3}s"
                        : "no solution";
                    _log(
                        $"[{layerNumber}/{totalLayers}] {l.Colour.DisplayName}: snake={snakeSink.TotalTime.TotalSeconds:F3}s, tsp={tspPart} -> {(usedSnake ? "snake" : "tsp")}"
                    );
                }

                // Bucket clicks. (The bucket outlines are merged into FineDetailPoints)
                if (l.BucketClicks.Count > 0)
                {
                    _log($"\tPerforming bucket fills: {l.BucketClicks.Count} clicks");

                    _toolbar.SelectBucket();
                    // tsp solve the points
                    var bucketClickRouteTimeout = 0.25f;
                    if (l.BucketClicks.Count > 50)
                        bucketClickRouteTimeout = 0.5f;
                    var optimizedBucketClickRoute = PerformTSP(
                        l.BucketClicks.ToList(),
                        bucketClickRouteTimeout
                    );
                    foreach (var click in optimizedBucketClickRoute ?? l.BucketClicks.ToList()) // in case somehow it fails
                    {
                        NavigateTo(_realOutput, click);
                        _realOutput.Tap(Button.A);
                        _realOutput.ReleaseAll();
                        _realOutput.Delay(1000); // Bucket fill can stutter; wait before moving so the next click does not desync.
                    }
                }
            }
            _log("Resetting selected colour to black...");
            _palette.SelectBlack(25.0);

            _log(
                $"Done! Total in layer draw time: {totalInLayerTime:F3}s (Doesnt include colour/brush selection)"
            );
        }

        private static bool IsBlankCanvasColour(PaletteColour colour) =>
            !colour.IsArbitrary && colour.R == 255 && colour.G == 255 && colour.B == 255;

        private static int RemoveEnclosedBlankCanvasRegions(
            List<ColourLayer> layers,
            int width,
            int height
        )
        {
            var whiteLayer = layers.FirstOrDefault(l => IsBlankCanvasColour(l.Colour));
            if (whiteLayer is null || whiteLayer.FineDetailPoints.Count == 0)
                return 0;

            var remainingPoints = new bool[width, height];
            foreach (var point in whiteLayer.FineDetailPoints)
                remainingPoints[point.X, point.Y] = true;

            var visited = new bool[width, height];
            int[] dx = { 0, 0, -1, 1 };
            int[] dy = { -1, 1, 0, 0 };
            int skippedRegions = 0;

            foreach (var seed in whiteLayer.FineDetailPoints.ToList())
            {
                if (visited[seed.X, seed.Y])
                    continue;

                var currentRegion = new List<CanvasPoint>();
                var q = new Queue<CanvasPoint>();
                bool touchesCanvasEdge = false;

                q.Enqueue(seed);
                visited[seed.X, seed.Y] = true;

                while (q.Count > 0)
                {
                    var current = q.Dequeue();
                    currentRegion.Add(current);
                    touchesCanvasEdge |=
                        current.X == 0
                        || current.Y == 0
                        || current.X == width - 1
                        || current.Y == height - 1;

                    for (int i = 0; i < 4; i++)
                    {
                        int tx = current.X + dx[i];
                        int ty = current.Y + dy[i];
                        if (
                            tx >= 0
                            && tx < width
                            && ty >= 0
                            && ty < height
                            && remainingPoints[tx, ty]
                            && !visited[tx, ty]
                        )
                        {
                            visited[tx, ty] = true;
                            q.Enqueue(new CanvasPoint(tx, ty));
                        }
                    }
                }

                // The canvas starts white, so an enclosed white component bounded by other colours
                // is already correct once those boundary colours are drawn. Drawing a white outline
                // before bucket-filling it only adds time and can overwrite the enclosing outline.
                if (!touchesCanvasEdge)
                {
                    foreach (var point in currentRegion)
                        whiteLayer.FineDetailPoints.Remove(point);
                    skippedRegions++;
                }
            }

            if (whiteLayer.FineDetailPoints.Count == 0)
            {
                layers.Remove(whiteLayer);
            }
            else if (skippedRegions > 0)
            {
                whiteLayer.Extents = new LayerExtents(
                    whiteLayer.FineDetailPoints.Min(p => p.X),
                    whiteLayer.FineDetailPoints.Max(p => p.X),
                    whiteLayer.FineDetailPoints.Min(p => p.Y),
                    whiteLayer.FineDetailPoints.Max(p => p.Y)
                );
            }

            return skippedRegions;
        }

        private static readonly int[] LargeBrushSizes = [27, 19, 13, 7, 3];

        // eviction thresholds are how many of that size there must be for it to commit to doing larger brushes over smaller ones.
        // bigger ones fill more area so they get more slack.
        // TODO: MORE WORK TWEAKING THESE!!!
        private static readonly int[] LargeBrushEvictionThreshold = [1, 1, 2, 6, 12];

        public static int DetectBucketZones(
            ColourLayer l,
            int width,
            int height,
            int minZoneSize = 36,
            int minBucketClickSafety = 2
        )
        {
            var workingSet = new bool[width, height];

            var outlinePixels = new List<CanvasPoint>();
            var interiorPixels = new bool[width, height];

            // used for testing neighbours.
            // the bucket fill in tomodachi life only works on direct neighbours, not diagonals.
            int[] dx = { 0, 0, -1, 1 };
            int[] dy = { -1, 1, 0, 0 };

            foreach (var p in l.FineDetailPoints)
                workingSet[p.X, p.Y] = true;

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (!workingSet[x, y])
                        continue;

                    bool isOutlinePixel = false;

                    // check up/down/left/right
                    for (int i = 0; i < 4; i++)
                    {
                        int tx = x + dx[i];
                        int ty = y + dy[i];

                        // handle edges as outline pixels.
                        if (tx < 0 || tx >= width || ty < 0 || ty >= height || !workingSet[tx, ty])
                        {
                            isOutlinePixel = true;
                            break;
                        }
                    }

                    if (isOutlinePixel)
                        outlinePixels.Add(new CanvasPoint(x, y));
                    else
                        interiorPixels[x, y] = true;
                }
            }

            var bucketClicks = new List<CanvasPoint>();
            var visited = new bool[width, height];

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (interiorPixels[x, y] && !visited[x, y])
                    {
                        // new zone
                        var currentZone = new List<CanvasPoint>();
                        var q = new Queue<CanvasPoint>();

                        var startNode = new CanvasPoint(x, y);
                        q.Enqueue(startNode);
                        visited[x, y] = true;

                        while (q.Count > 0)
                        {
                            var current = q.Dequeue();
                            currentZone.Add(current);

                            // same dealio
                            for (int i = 0; i < 4; i++)
                            {
                                int tx = current.X + dx[i];
                                int ty = current.Y + dy[i];

                                if (
                                    tx >= 0
                                    && tx < width
                                    && ty >= 0
                                    && ty < height
                                    && interiorPixels[tx, ty]
                                    && !visited[tx, ty]
                                )
                                {
                                    visited[tx, ty] = true;
                                    q.Enqueue(new CanvasPoint(tx, ty));
                                }
                            }
                        }

                        // Reject uselessly small ones, and only bucket-fill zones that have a
                        // click point safely inside the outline. Clicking the first interior pixel
                        // can be directly next to the boundary; if the cursor is delayed by one
                        // step, the bucket can hit outside the intended area and flood the image.
                        if (currentZone.Count >= minZoneSize)
                        {
                            var safestClick = FindSafestBucketClick(
                                currentZone,
                                interiorPixels,
                                width,
                                height,
                                dx,
                                dy,
                                out int clickSafety
                            );

                            if (clickSafety >= minBucketClickSafety)
                            {
                                bucketClicks.Add(safestClick);
                            }
                            else
                            {
                                outlinePixels.AddRange(currentZone);
                            }
                        }
                        else
                        {
                            outlinePixels.AddRange(currentZone);
                        }
                    }
                }
            }

            // outlinePixels also contains the rejects by the end, bit misleading.
            l.FineDetailPoints.Clear();
            l.FineDetailPoints.UnionWith(outlinePixels);

            l.BucketClicks = new HashSet<CanvasPoint>(bucketClicks);

            return l.BucketClicks.Count;
        }

        private static CanvasPoint FindSafestBucketClick(
            List<CanvasPoint> zone,
            bool[,] interiorPixels,
            int width,
            int height,
            int[] dx,
            int[] dy,
            out int safety
        )
        {
            var distances = new int[width, height];
            var q = new Queue<CanvasPoint>();

            foreach (var point in zone)
            {
                bool isInteriorEdge = false;
                for (int i = 0; i < 4; i++)
                {
                    int tx = point.X + dx[i];
                    int ty = point.Y + dy[i];
                    if (
                        tx < 0
                        || tx >= width
                        || ty < 0
                        || ty >= height
                        || !interiorPixels[tx, ty]
                    )
                    {
                        isInteriorEdge = true;
                        break;
                    }
                }

                if (isInteriorEdge)
                {
                    distances[point.X, point.Y] = 1;
                    q.Enqueue(point);
                }
            }

            while (q.Count > 0)
            {
                var current = q.Dequeue();
                int nextDistance = distances[current.X, current.Y] + 1;

                for (int i = 0; i < 4; i++)
                {
                    int tx = current.X + dx[i];
                    int ty = current.Y + dy[i];
                    if (
                        tx >= 0
                        && tx < width
                        && ty >= 0
                        && ty < height
                        && interiorPixels[tx, ty]
                        && distances[tx, ty] == 0
                    )
                    {
                        distances[tx, ty] = nextDistance;
                        q.Enqueue(new CanvasPoint(tx, ty));
                    }
                }
            }

            CanvasPoint safestClick = zone[0];
            safety = distances[safestClick.X, safestClick.Y];
            foreach (var point in zone)
            {
                int distance = distances[point.X, point.Y];
                if (distance > safety)
                {
                    safety = distance;
                    safestClick = point;
                }
            }

            return safestClick;
        }

        /// <summary>Takes in a ColourLayer and detects large areas that can be better drawn with stamps.</summary>
        /// <param name="l"></param>
        public void DetectUniformAreas(ColourLayer l, int width, int height)
        {
            // NOTES:
            // 3x3 Brushes seem to be past the point of diminishing returns,
            // will probably dump those unless there a good number of them?
            // TODO: That ^

            // need to build a more useful 2d array for scanning since l.FineDetailPoints is uh well, just a hashset of points.
            var points = new bool[width, height];
            foreach (var p in l.FineDetailPoints)
                points[p.X, p.Y] = true;

            // So:
            // When we find a good stampable area, we need to remove it from consideration (from the bool[,] array)
            // and also remove those points from the fine detail pass.

            l.StampsBySize = new Dictionary<int, List<CanvasPoint>>();

            _log($"Scanning {l.Colour.DisplayName} for large brush");

            foreach (var brushSize in LargeBrushSizes)
            {
                int half = brushSize / 2; // rounds down. which is fine.
                // TODO: Pickup from here.
                var largeBrushPoints = new List<CanvasPoint>();
                for (int x = half; x < width - half; x++)
                {
                    for (int y = half; y < height - half; y++)
                    {
                        var isUniform = IsUniformArea(points, x, y, brushSize);
                        if (isUniform)
                        {
                            largeBrushPoints.Add(new CanvasPoint(x, y));
                            // Remove it from FineDetail and our consideration map (points)
                            ClearStampArea(points, l.FineDetailPoints, x, y, brushSize);
                            // continue onwards to find more points :3
                        }
                    }
                }

                if (largeBrushPoints.Count == 0)
                    continue;

                // Evict lone stamps or small amounts of them
                // The overhead of going to them is generally not worth it.

                int indexOfBrushSize = Array.IndexOf(LargeBrushSizes, brushSize);
                if (largeBrushPoints.Count < LargeBrushEvictionThreshold[indexOfBrushSize])
                {
                    _log(
                        $"\tEVICTED {largeBrushPoints.Count} areas for size {brushSize}^2 because too few."
                    );
                    // un-clear the area.
                    foreach (var p in largeBrushPoints)
                    {
                        RefillStampArea(points, l.FineDetailPoints, p.X, p.Y, brushSize);
                    }
                    continue;
                }

                l.StampsBySize[brushSize] = largeBrushPoints;
                _log($"\tFOUND {largeBrushPoints.Count} areas for size {brushSize}^2");
            }
        }

        private static bool IsUniformArea(bool[,] map, int cx, int cy, int brushSize)
        {
            int half = brushSize / 2; // rounds down.
            for (int dy = -half; dy <= half; dy++)
            {
                for (int dx = -half; dx <= half; dx++)
                    if (!map[cx + dx, cy + dy])
                        return false;
            }

            return true;
        }

        private static void ClearStampArea(
            bool[,] map,
            HashSet<CanvasPoint> points,
            int cx,
            int cy,
            int brushSize
        )
        {
            int half = brushSize / 2;
            for (int dy = -half; dy <= half; dy++)
            {
                for (int dx = -half; dx <= half; dx++)
                {
                    points.Remove(new CanvasPoint(cx + dx, cy + dy));
                    map[cx + dx, cy + dy] = false;
                }
            }
        }

        private static void RefillStampArea(
            bool[,] map,
            HashSet<CanvasPoint> points,
            int cx,
            int cy,
            int brushSize
        )
        {
            int half = brushSize / 2;
            for (int dy = -half; dy <= half; dy++)
            {
                for (int dx = -half; dx <= half; dx++)
                {
                    points.Add(new CanvasPoint(cx + dx, cy + dy));
                    map[cx + dx, cy + dy] = true;
                }
            }
        }

        private void FineDetailSnake(ISwitchOutput output, ColourLayer l)
        {
            // find the nearest edge.
            int topLeft = MeasureDistanceToFromCurrent(l.Extents.MinX, l.Extents.MinY);
            int topRight = MeasureDistanceToFromCurrent(l.Extents.MaxX, l.Extents.MinY);
            int bottomLeft = MeasureDistanceToFromCurrent(l.Extents.MinX, l.Extents.MaxY);
            int bottomRight = MeasureDistanceToFromCurrent(l.Extents.MaxX, l.Extents.MaxY);

            int bestDist = Math.Min(Math.Min(topLeft, topRight), Math.Min(bottomLeft, bottomRight));

            bool goingDown = false;
            bool goingRight = false;

            // todo: probably a cleaner way to match.
            if (topLeft == bestDist)
            {
                NavigateTo(output, l.Extents.MinX, l.Extents.MinY);
                goingRight = true;
                goingDown = true;
            }
            else if (topRight == bestDist)
            {
                NavigateTo(output, l.Extents.MaxX, l.Extents.MinY);
                goingRight = false;
                goingDown = true;
            }
            else if (bottomLeft == bestDist)
            {
                NavigateTo(output, l.Extents.MinX, l.Extents.MaxY);
                goingRight = true;
                goingDown = false;
            }
            else // br
            {
                NavigateTo(output, l.Extents.MaxX, l.Extents.MaxY);
                goingRight = false;
                goingDown = false;
            }

            // <TODO: MILD AI SLOP, REVIEW!!!!
            int startY = goingDown ? l.Extents.MinY : l.Extents.MaxY;
            int endY = goingDown ? l.Extents.MaxY : l.Extents.MinY;
            int yStep = goingDown ? 1 : -1;

            for (int y = startY; goingDown ? y <= endY : y >= endY; y += yStep)
            {
                if (!l.FineDetailPoints.Any(p => p.Y == y))
                {
                    // If theres literally nothing remaining then we dont even bother going up or down
                    // in the event that doing so would get us further from the next point, it also just wastes
                    // up/down inputs
                    var isThereAnyAtAllLeft = l.FineDetailPoints.Any(p =>
                        goingDown ? p.Y > y : p.Y < y
                    );
                    if (y != endY && isThereAnyAtAllLeft)
                    {
                        if (goingDown)
                        {
                            output.Tap(DPad.DOWN);
                            _cursorY++;
                        }
                        else
                        {
                            output.Tap(DPad.UP);
                            _cursorY--;
                        }
                    }
                    // If there is !isThereAnyAtAllLeft we are just done.
                    continue;
                }

                // everything for the for loop
                // only goes left/right to the extents of the layer (TODO: Do just for the row! Would need to NavigateTo for first on next row tho)
                // and goes the correct direction, which is only flipped when we actually do a row.
                //int startX = goingRight ? l.Extents.MinX : l.Extents.MaxX;
                //int endX = goingRight ? l.Extents.MaxX : l.Extents.MinX;

                int startX,
                    endX;
                if (goingRight)
                {
                    startX = l.FineDetailPoints.Where(p => p.Y == y).Min(p => p.X);
                    endX = l.FineDetailPoints.Where(p => p.Y == y).Max(p => p.X);
                }
                else
                {
                    startX = l.FineDetailPoints.Where(p => p.Y == y).Max(p => p.X);
                    endX = l.FineDetailPoints.Where(p => p.Y == y).Min(p => p.X);
                }

                int xStep = goingRight ? 1 : -1;
                bool holdingA = false;

                // since our x extents change each y layer, need to NavigateTo the start point
                var firstPoint = new CanvasPoint(startX, y);
                NavigateTo(output, firstPoint);

                for (int x = startX; goingRight ? x <= endX : x >= endX; x += xStep)
                {
                    bool isCurrentPoint = l.FineDetailPoints.Contains(new CanvasPoint(x, y));
                    if (isCurrentPoint && !holdingA)
                    {
                        output.Press(Button.A);
                        output.Delay(25.0);
                        holdingA = true;
                    }

                    if (x == endX)
                    {
                        if (holdingA)
                        {
                            output.Release(Button.A);
                            output.Delay(25.0);
                            holdingA = false;
                        }
                        break;
                    }

                    bool isNextPoint = l.FineDetailPoints.Contains(new CanvasPoint(x + xStep, y));
                    if (holdingA && !isNextPoint)
                    {
                        output.Release(Button.A);
                        output.Delay(25.0);
                        holdingA = false;
                    }

                    if (goingRight)
                    {
                        output.Tap(DPad.RIGHT);
                        _cursorX++;
                    }
                    else
                    {
                        output.Tap(DPad.LEFT);
                        _cursorX--;
                    }
                }

                goingRight = !goingRight;

                if (y != endY)
                {
                    if (goingDown)
                    {
                        output.Tap(DPad.DOWN);
                        _cursorY++;
                    }
                    else
                    {
                        output.Tap(DPad.UP);
                        _cursorY--;
                    }
                }
            }
        }

        // TSP with Google.OrTools nuget package
        // https://developers.google.com/optimization/routing/tsp#c_1
        // It is possible for it to NOT find a solution.
        private void FineDetailTsp(ISwitchOutput output, ColourLayer l, float timeLimitSeconds)
        {
            // Find start point, this logic will need adjusted in time
            // when we eventually reorder layers to be the most optimal.

            var optimizedRoute = PerformTSP(l.FineDetailPoints.ToList(), timeLimitSeconds);

            if (optimizedRoute == null)
                return;

            // Navigate through the optimised route.
            // A is held across orthogonally-adjacent pixels only. Diagonal held strokes can make
            // the pixel-perfect brush drift or clip past the canvas edge, so diagonal neighbours
            // are treated as separate taps.
            bool isAHeld = false;
            for (int idx = 0; idx < optimizedRoute.Count; idx++)
            {
                var point = optimizedRoute[idx];
                NavigateTo(output, point);

                bool nextIsAdjacent =
                    idx + 1 < optimizedRoute.Count
                    && Math.Abs(optimizedRoute[idx + 1].X - point.X)
                        + Math.Abs(optimizedRoute[idx + 1].Y - point.Y)
                        == 1;

                if (isAHeld)
                {
                    // We arrived here via a 1-step NavigateTo with A held, which painted this cell.
                    if (!nextIsAdjacent)
                    {
                        output.Release(Button.A);
                        output.Delay(25.0);
                        isAHeld = false;
                    }
                    // else: next is also adjacent, keep holding
                }
                else if (nextIsAdjacent)
                {
                    // Start of an adjacent run — press and hold.
                    output.Press(Button.A);
                    output.Delay(25.0);
                    isAHeld = true;
                }
                else
                {
                    // Isolated point — plain tap, no hold.
                    output.Tap(Button.A);
                }
            }
        }

        private List<CanvasPoint>? PerformTSP(List<CanvasPoint> inputPoints, float timeLimitSeconds)
        {
            if (inputPoints.Count <= 1)
                return [.. inputPoints];

            var points = inputPoints.ToArray();

            const int originNode = 0;
            int dummyEndNode = points.Length + 1;
            int nodeCount = points.Length + 2;

            // Model drawing as an open path: start at the current cursor, visit every point once,
            // then finish anywhere. The dummy end node has zero inbound cost, which avoids the
            // old closed-loop TSP bias that optimized an unnecessary return to the starting point.
            var manager = new RoutingIndexManager(nodeCount, 1, [originNode], [dummyEndNode]);
            var routing = new RoutingModel(manager);

            int transitCallbackIndex = routing.RegisterTransitCallback(
                (fromIndex, toIndex) =>
                {
                    int fromNode = manager.IndexToNode(fromIndex);
                    int toNode = manager.IndexToNode(toIndex);

                    if (toNode == dummyEndNode || fromNode == dummyEndNode || toNode == originNode)
                        return 0;

                    if (fromNode == originNode)
                    {
                        var toPoint = points[toNode - 1];
                        return MeasureDistanceToFromCurrent(toPoint.X, toPoint.Y);
                    }

                    var fromPoint = points[fromNode - 1];
                    var nextPoint = points[toNode - 1];
                    return MeasureDistance(fromPoint, nextPoint);
                }
            );

            routing.SetArcCostEvaluatorOfAllVehicles(transitCallbackIndex);

            var searchParameters =
                operations_research_constraint_solver.DefaultRoutingSearchParameters();
            searchParameters.FirstSolutionStrategy = FirstSolutionStrategy
                .Types
                .Value
                .PathCheapestArc;
            searchParameters.LocalSearchMetaheuristic = LocalSearchMetaheuristic
                .Types
                .Value
                .GuidedLocalSearch;
            // need to get int seconds and int nanoseconds because... google.
            int seconds = (int)timeLimitSeconds;
            int nanoseconds = (int)((timeLimitSeconds - seconds) * 1_000_000_000);
            searchParameters.TimeLimit = new Google.Protobuf.WellKnownTypes.Duration
            {
                Seconds = seconds,
                Nanos = nanoseconds,
            };

            var sw = Stopwatch.StartNew();
            var solution = routing.SolveWithParameters(searchParameters);
            sw.Stop();

            if (solution is null)
                return null;

            var optimizedRoute = new List<CanvasPoint>(points.Length);
            long index = routing.Start(0);
            while (routing.IsEnd(index) == false)
            {
                int node = manager.IndexToNode(index);
                if (node != originNode)
                    optimizedRoute.Add(points[node - 1]);

                index = solution.Value(routing.NextVar(index));
            }

            return optimizedRoute;
        }

        private static int MeasureDistance(CanvasPoint from, CanvasPoint to) =>
            Math.Max(Math.Abs(from.X - to.X), Math.Abs(from.Y - to.Y));

        private void NavigateTo(ISwitchOutput output, CanvasPoint p) =>
            NavigateTo(output, p.X, p.Y);

        private void NavigateTo(ISwitchOutput output, int targetX, int targetY)
        {
            if (targetX < 0 || targetX >= CanvasWidth || targetY < 0 || targetY >= CanvasHeight)
                throw new ArgumentOutOfRangeException(
                    nameof(targetX),
                    $"Target point ({targetX}, {targetY}) is outside the {CanvasWidth}x{CanvasHeight} canvas."
                );

            // Diaganols.
            while (_cursorX != targetX && _cursorY != targetY)
            {
                var dir = (_cursorX < targetX, _cursorY < targetY) switch
                {
                    (true, true) => DPad.DOWNRIGHT,
                    (true, false) => DPad.UPRIGHT,
                    (false, true) => DPad.DOWNLEFT,
                    (false, false) => DPad.UPLEFT,
                };

                output.Tap(dir);
                _cursorX += _cursorX < targetX ? 1 : -1;
                _cursorY += _cursorY < targetY ? 1 : -1;
            }

            // Finish off remainder.
            NavigateX(output, targetX);
            NavigateY(output, targetY);
        }

        private void NavigateX(ISwitchOutput output, int targetX)
        {
            while (_cursorX < targetX)
            {
                output.Tap(DPad.RIGHT);
                _cursorX++;
            }

            while (_cursorX > targetX)
            {
                output.Tap(DPad.LEFT);
                _cursorX--;
            }
        }

        private void NavigateY(ISwitchOutput output, int targetY)
        {
            while (_cursorY < targetY)
            {
                output.Tap(DPad.DOWN);
                _cursorY++;
            }

            while (_cursorY > targetY)
            {
                output.Tap(DPad.UP);
                _cursorY--;
            }
        }

        /// <summary>
        /// Calculates the number of button inputs to navigate to a point, without any A presses.
        /// <para>This is just the longest distance on axis since diagonal inputs negate any loss</para>
        /// </summary>
        /// <returns>Distance in button presses</returns>
        private int MeasureDistanceToFromCurrent(int targetX, int targetY)
        {
            return Math.Max(Math.Abs(_cursorX - targetX), Math.Abs(_cursorY - targetY));
        }

        // TODO: Verify this before enabling. The expected starting point is the drawing canvas
        // with the cursor already focused on the Basic/Pro mode toggle. If the focus is anywhere
        // else, these inputs may select the wrong control and desync the draw.
        // private void SwitchBasicModeToProMode()
        // {
        //     _realOutput.Tap(Button.X, 100, 50); // Open the drawing UI/options area.
        //     _realOutput.Delay(500);
        //     _realOutput.Tap(DPad.UP, 50, 50);
        //     _realOutput.Tap(DPad.LEFT, 50, 50);
        //     _realOutput.Tap(Button.A, 100, 50); // Toggle Basic -> Pro.
        //     _realOutput.Delay(750);
        //     _realOutput.Tap(Button.B, 100, 50); // Return to the canvas before drawing.
        //     _realOutput.Delay(500);
        // }

        public void ConnectAndConfirmController()
        {
            _realOutput.Tap(Button.A, 100, 50);
            _realOutput.Delay(1750); // raised from 1000ms to 1750 for switch 1
            _realOutput.Tap(Button.A, 500, 50);
            _realOutput.Delay(750); // raised from 500ms to 750ms for switch 1
            _realOutput.Tap(Button.A, 750, 50);
            _realOutput.Delay(2000); // raised from 1500 to 2000 for switch 1
        }
    }
}
