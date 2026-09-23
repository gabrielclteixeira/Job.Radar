using JobRadar;

namespace JobRadar.Tests;

public class ScorerParseTests
{
    private static AiResult?[] Parse(string raw, int n)
    {
        var results = new AiResult?[n];
        ClaudeScorer.ParseBatch(raw, results);
        return results;
    }

    [Fact]
    public void Well_formed_array_maps_by_i()
    {
        var r = Parse("""[{"i":2,"score":80,"verdict":"b","reasons":["x"],"redFlags":[]},{"i":1,"score":40,"verdict":"a","reasons":[],"redFlags":[]}]""", 2);
        Assert.Equal(40, r[0]!.Score);
        Assert.Equal(80, r[1]!.Score);
    }

    [Fact]
    public void Truncated_reply_keeps_the_complete_objects()
    {
        string raw = """Here you go: [{"i":1,"score":70,"verdict":"ok {nice}","reasons":["a"],"redFlags":[]},{"i":2,"score":55,"verdict":"meh","reasons":[],"redFlags":[]},{"i":3,"score":9""";
        var r = Parse(raw, 3);
        Assert.Equal(70, r[0]!.Score);
        Assert.Equal("ok {nice}", r[0]!.Verdict);
        Assert.Equal(55, r[1]!.Score);
        Assert.Null(r[2]);
    }

    [Fact]
    public void Zero_based_i_is_not_shifted()
    {
        var r = Parse("""[{"i":0,"score":10,"verdict":"a"},{"i":1,"score":20,"verdict":"b"}]""", 2);
        Assert.Equal(10, r[0]!.Score);
        Assert.Equal(20, r[1]!.Score);
    }

    [Fact]
    public void Non_string_reasons_do_not_throw_and_decimal_scores_round()
    {
        var r = Parse("""[{"i":1,"score":72.6,"verdict":"a","reasons":["ok", 3, {"x":1}],"redFlags":null}]""", 1);
        Assert.Equal(73, r[0]!.Score);
        Assert.Equal(new[] { "ok" }, r[0]!.Reasons);
    }

    [Fact]
    public void Bare_object_for_a_batch_of_one_and_bare_keys()
    {
        var r = Parse("""{i: 1, score: 61, verdict: "fine"}""", 1);
        Assert.Equal(61, r[0]!.Score);
    }

    [Fact]
    public void Garbage_leaves_everything_null()
        => Assert.All(Parse("sorry, I can't help with that", 3), Assert.Null);
}
