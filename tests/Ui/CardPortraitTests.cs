using System;
using System.Collections.Generic;
using Xunit;
using STS2_DamageCharts.Ui;

public class CardPortraitTests
{
    private sealed class Card
    {
        public string? PortraitPath { get; init; }
        public string? BetaPortraitPath { get; init; }
    }

    private sealed class OldCard   // a game version without BetaPortraitPath
    {
        public string? PortraitPath { get; init; }
    }

    private sealed class ThrowingCard
    {
        public string PortraitPath => throw new InvalidOperationException("no model");
        public string BetaPortraitPath => "res://beta/strike.png";
    }

    private const string Final = "res://images/cards/strike.png";
    private const string Beta = "res://images/cards/beta/strike.png";

    private static Func<string, bool> Have(params string[] paths) => new HashSet<string>(paths).Contains;

    [Fact]
    public void PrefersFinalArtWhenItExists()
    {
        var card = new Card { PortraitPath = Final, BetaPortraitPath = Beta };
        Assert.Equal(Final, CardPortrait.Pick(card, Have(Final, Beta)));
    }

    [Fact]
    public void FallsBackToPlaceholderArt()
    {
        var card = new Card { PortraitPath = Final, BetaPortraitPath = Beta };
        Assert.Equal(Beta, CardPortrait.Pick(card, Have(Beta)));
    }

    [Fact]
    public void NullWhenNeitherExistsOrNoCard()
    {
        Assert.Null(CardPortrait.Pick(new Card { PortraitPath = Final, BetaPortraitPath = Beta }, Have()));
        Assert.Null(CardPortrait.Pick(new Card { PortraitPath = "", BetaPortraitPath = null }, _ => true));
        Assert.Null(CardPortrait.Pick(null, _ => true));
    }

    [Fact]
    public void WorksWithoutBetaPortraitPathAndSurvivesThrowingGetters()
    {
        Assert.Equal(Final, CardPortrait.Pick(new OldCard { PortraitPath = Final }, Have(Final)));
        Assert.Null(CardPortrait.Pick(new OldCard { PortraitPath = Final }, Have(Beta)));
        Assert.Equal("res://beta/strike.png", CardPortrait.Pick(new ThrowingCard(), _ => true));
    }

    [Fact]
    public void ExistsCheckThatThrowsIsAMiss()
    {
        var card = new Card { PortraitPath = Final, BetaPortraitPath = Beta };
        Assert.Equal(Beta, CardPortrait.Pick(card, p => p == Final ? throw new Exception("io") : true));
    }
}
