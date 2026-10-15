using AvaMedia.Core;
using Xunit;

public sealed class PresentationTests
{
    [Theory]
    [InlineData(0, "00:00.0")]
    [InlineData(71.145941, "01:11.1")]
    [InlineData(59.96, "01:00.0")]
    [InlineData(3599.96, "1:00:00.0")]
    [InlineData(3661.15, "1:01:01.2")]
    [InlineData(90061.1, "25:01:01.1")]
    public void Time_displays_one_decimal_and_carries_across_minutes_and_hours(double seconds, string expected)
        => Assert.Equal(expected, PersonClipPresentation.Time(seconds));

    [Theory]
    [InlineData("h264", "H.264")]
    [InlineData("H264", "H.264")]
    [InlineData("hevc", "H.265")]
    [InlineData("h265", "H.265")]
    [InlineData("av1", "AV1")]
    public void Codec_uses_display_names(string codec, string expected)
        => Assert.Equal(expected, PersonClipPresentation.Codec(codec));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(10613762, "10 MB")]
    [InlineData(1610612736, "1.5 GB")]
    public void File_size_uses_human_readable_units(long bytes, string expected)
        => Assert.Equal(expected, PersonClipPresentation.Bytes(bytes));

    [Fact]
    public void Source_and_export_match_stage_display_examples()
    {
        var info = new MediaInfo(71.145941, 1280, 720, true, true, "{}", "h264");
        Assert.Equal("1280×720 · H.264 · 01:11.1", PersonClipPresentation.Source(info));
        Assert.Equal("01:11.1 · 10 MB", PersonClipPresentation.Export(info.Duration, 10613762));
    }

    [Fact]
    public void Runtime_collapses_shared_backends_and_translates_fallback_notes()
    {
        PersonDetectorStatistics[] detectors = [Detector("NanoDet"), Detector("MediaPipe")];
        Assert.Equal("CPU", PersonClipPresentation.Backend(detectors));
        Assert.Equal("未检测到可用 GPU 执行器", PersonClipPresentation.Note(detectors));
        Assert.Equal("NanoDet · CPU", PersonClipPresentation.ModelDetail(detectors.Take(1).ToArray()));
        Assert.Equal("NanoDet CPU · MediaPipe CPU", PersonClipPresentation.ModelDetail(detectors));
        detectors[1] = detectors[1] with { Backend = "Core ML / CPU", FallbackReason = null };
        Assert.Equal("NanoDet CPU · MediaPipe Core ML / CPU", PersonClipPresentation.Backend(detectors));
        Assert.Equal("", PersonClipPresentation.Note([detectors[1]]));
        Assert.Equal("", PersonClipPresentation.Backend([]));
    }

    [Theory]
    [InlineData("Core ML execution provider is unavailable.", "Core ML 执行器不可用")]
    [InlineData("DirectML execution provider is unavailable.", "DirectML 执行器不可用")]
    [InlineData("model execution error", "GPU 执行失败，已回退 CPU：model execution error")]
    public void Fallback_keeps_a_Chinese_summary_and_error_diagnostics(string reason, string expected)
        => Assert.Equal(expected, PersonClipPresentation.Fallback(reason));

    private static PersonDetectorStatistics Detector(string name) => new(name, name, 0, 0, "CPU",
        "No supported GPU execution provider on this platform.");
}

public sealed class PreviewGeometryTests
{
    [Theory]
    [InlineData(1280, 720, 416, 0, 91, 416, 234)]
    [InlineData(720, 1280, 416, 91, 0, 234, 416)]
    [InlineData(1280, 720, 224, 0, 49, 224, 126)]
    [InlineData(1280, 720, 640, 0, 140, 640, 360)]
    [InlineData(1000, 561, 416, 0, 91, 416, 233)]
    [InlineData(416, 416, 416, 0, 0, 416, 416)]
    public void Preview_content_removes_only_the_decode_letterbox(int width, int height, int size,
        int left, int top, int contentWidth, int contentHeight)
    {
        var info = new MediaInfo(10, width, height, true, false, "{}", "h264");
        Assert.Equal((left, top, contentWidth, contentHeight), PersonClipPreview.Content(info, size));
    }

    [Theory]
    [InlineData(ModelCatalog.NanoDetId, 1280, 720, .25, .359375, .5, .28125)]
    [InlineData(ModelCatalog.MediaPipePersonId, 1280, 720, .25, .359375, .5, .28125)]
    [InlineData(ModelCatalog.PersonId, 1280, 720, .25, .140625, .5, .28125)]
    [InlineData(ModelCatalog.NanoDetId, 720, 1280, .359375, .25, .28125, .5)]
    [InlineData(ModelCatalog.MediaPipePersonId, 720, 1280, .359375, .25, .28125, .5)]
    [InlineData(ModelCatalog.PersonId, 720, 1280, .140625, .25, .28125, .5)]
    [InlineData(ModelCatalog.NanoDetId, 416, 416, .25, .25, .5, .5)]
    public void Boxes_use_unpadded_coordinates_for_each_detector(string id, int width, int height,
        double x, double y, double boxWidth, double boxHeight)
    {
        var content = PersonClipPreview.Content(new(10, width, height, true, false, "{}", "h264"), 640);
        var box = Assert.Single(PersonDetectionGeometry.ToPreview([new(x, y, boxWidth, boxHeight, .9)], PersonDetectorCatalog.Find(id), content));
        Assert.Equal(.25, box.X, 10); Assert.Equal(.25, box.Y, 10);
        Assert.Equal(.5, box.Width, 10); Assert.Equal(.5, box.Height, 10);
        Assert.Equal(.9, box.Confidence); Assert.Equal("人物", box.Label);
    }

    [Fact]
    public void Boxes_are_clipped_to_content_and_padding_only_detections_are_removed()
    {
        var content = PersonClipPreview.Content(new(10, 1280, 720, true, false, "{}", "h264"), 416);
        var boxes = PersonDetectionGeometry.ToPreview([new(0, 0, 1, 1, .9), new(.1, 0, .2, .1, .8)],
            PersonDetectorCatalog.Find(ModelCatalog.NanoDetId), content);
        var box = Assert.Single(boxes);
        Assert.Equal(new PersonClipDetectionBox(0, 0, 1, 1, .9), box);
    }
}
