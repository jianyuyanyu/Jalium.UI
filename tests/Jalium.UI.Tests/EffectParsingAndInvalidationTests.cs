using Jalium.UI.Controls;
using Jalium.UI.Markup;
using Jalium.UI.Media;
using Jalium.UI.Media.Effects;

namespace Jalium.UI.Tests;

[Collection("Application")]
public sealed class EffectParsingAndInvalidationTests
{
    [Fact]
    public void XamlReader_ParsesEveryBuiltInElementEffectInsideEffectGroup()
    {
        const string xaml = """
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Border.Effect>
                    <EffectGroup>
                        <BlurEffect Radius="3" KernelType="Box" />
                        <ElementBlurEffect Radius="4" RenderingBias="Quality" />
                        <DropShadowEffect BlurRadius="8" ShadowDepth="2" Color="#80402010" Opacity="0.5" />
                        <OuterGlowEffect GlowSize="7" GlowColor="#6034D399" Opacity="0.75" Intensity="1.25" />
                        <InnerShadowEffect BlurRadius="6" ShadowDepth="3" Color="#40112233" Opacity="0.8" />
                        <EmbossEffect Amount="1.5" LightAngle="30" Relief="0.6" Width="2" />
                        <ColorMatrixEffect Matrix="0.2126 0.7152 0.0722 0 0; 0.2126 0.7152 0.0722 0 0; 0.2126 0.7152 0.0722 0 0; 0 0 0 1 0" />
                    </EffectGroup>
                </Border.Effect>
            </Border>
            """;

        var border = Assert.IsType<Border>(XamlReader.Parse(xaml));
        var group = Assert.IsType<EffectGroup>(border.Effect);

        Assert.Collection(
            group.Children,
            effect =>
            {
                var blur = Assert.IsType<BlurEffect>(effect);
                Assert.Equal(3, blur.Radius);
                Assert.Equal(KernelType.Box, blur.KernelType);
            },
            effect =>
            {
                var blur = Assert.IsType<ElementBlurEffect>(effect);
                Assert.Equal(4, blur.Radius);
                Assert.Equal(RenderingBias.Quality, blur.RenderingBias);
            },
            effect => Assert.Equal(8, Assert.IsType<DropShadowEffect>(effect).BlurRadius),
            effect => Assert.Equal(7, Assert.IsType<OuterGlowEffect>(effect).GlowSize),
            effect => Assert.Equal(6, Assert.IsType<InnerShadowEffect>(effect).BlurRadius),
            effect => Assert.Equal(1.5, Assert.IsType<EmbossEffect>(effect).Amount),
            effect => Assert.False(Assert.IsType<ColorMatrixEffect>(effect).Matrix.IsIdentity));
    }

    [Fact]
    public void XamlReader_ParsesCompositeBackdropChildrenAndKeepsAggregateLive()
    {
        const string xaml = """
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Border.BackdropEffect>
                    <CompositeBackdropEffect>
                        <BackdropBlurEffect BlurRadius="18" BlurSigma="6" />
                        <ColorAdjustmentEffect Saturation="0.75" Luminosity="0.9" NoiseIntensity="0.04" />
                    </CompositeBackdropEffect>
                </Border.BackdropEffect>
            </Border>
            """;

        var border = Assert.IsType<Border>(XamlReader.Parse(xaml));
        var composite = Assert.IsType<CompositeBackdropEffect>(border.BackdropEffect);
        Assert.Equal(2, composite.Effects.Count);
        Assert.Equal(18f, composite.BlurRadius);
        Assert.Equal(0.75f, composite.Saturation);
        Assert.Equal(0.9f, composite.Luminosity);
        Assert.Equal(0.04f, composite.NoiseIntensity);
        Assert.True(composite.HasEffect);

        border.ClearRenderDirty();
        Assert.False(border.IsRenderDirty);

        var blur = Assert.IsType<BackdropBlurEffect>(composite.Effects[0]);
        blur.BlurRadius = 27f;

        Assert.Equal(27f, composite.BlurRadius);
        Assert.True(border.IsRenderDirty);
    }

    [Fact]
    public void XamlReader_ParsesLegacyBitmapEffectGroupContent()
    {
#pragma warning disable CS0618
        const string xaml = """
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Border.BitmapEffect>
                    <BitmapEffectGroup>
                        <BlurBitmapEffect Radius="4" />
                        <OuterGlowBitmapEffect GlowSize="6" Opacity="0.5" />
                    </BitmapEffectGroup>
                </Border.BitmapEffect>
            </Border>
            """;

        var border = Assert.IsType<Border>(XamlReader.Parse(xaml));
        var group = Assert.IsType<BitmapEffectGroup>(border.BitmapEffect);
        Assert.Collection(
            Assert.IsType<BitmapEffectCollection>(group.Children),
            effect => Assert.Equal(4, Assert.IsType<BlurBitmapEffect>(effect).Radius),
            effect => Assert.Equal(6, Assert.IsType<OuterGlowBitmapEffect>(effect).GlowSize));
#pragma warning restore CS0618
    }

    [Fact]
    public void EffectGroup_DeepClonesFreezesAndForwardsChildChanges()
    {
        var blur = new BlurEffect { Radius = 3 };
        var group = new EffectGroup();
        group.Children.Add(blur);

        var owner = new Border { Effect = group };
        owner.ClearRenderDirty();
        blur.Radius = 5;
        Assert.True(owner.IsRenderDirty);

        var clone = group.Clone();
        Assert.NotSame(group.Children, clone.Children);
        var clonedBlur = Assert.IsType<BlurEffect>(Assert.Single(clone.Children));
        Assert.NotSame(blur, clonedBlur);
        Assert.Equal(5, clonedBlur.Radius);

        clone.Freeze();
        Assert.True(clone.Children.IsFrozen);
        Assert.True(clonedBlur.IsFrozen);
        Assert.Throws<InvalidOperationException>(() => clone.Children.Add(new BlurEffect()));
    }

    [Fact]
    public void EffectGroups_RejectReferenceCycles_AndBackdropGroupsRemainSafe()
    {
        var first = new EffectGroup();
        var second = new EffectGroup();
        first.Children.Add(second);
        var exception = Assert.Throws<InvalidOperationException>(() => second.Children.Add(first));
        Assert.Contains("cannot contain itself", exception.Message);
        Assert.Empty(second.Children);

        first.Children.Add(new BlurEffect(2));

        Assert.True(first.HasEffect);
        Assert.False(second.HasEffect);
        Assert.Equal(new Thickness(2), first.EffectPadding);
        Assert.Equal(Thickness.Zero, second.EffectPadding);

        var composite = new CompositeBackdropEffect();
        composite.Effects.Add(composite);
        Assert.False(composite.HasEffect);

        composite.Effects.Add(new BackdropBlurEffect { BlurRadius = 7 });
        Assert.True(composite.HasEffect);
        Assert.Equal(7, composite.BlurRadius);
    }

    [Fact]
    public void XamlTypeRegistry_ContainsAllBuiltInEffectObjectElements()
    {
        Type[] expectedTypes =
        [
            typeof(BlurEffect),
            typeof(ElementBlurEffect),
            typeof(DropShadowEffect),
            typeof(OuterGlowEffect),
            typeof(InnerShadowEffect),
            typeof(EmbossEffect),
            typeof(ColorMatrixEffect),
            typeof(EffectGroup),
            typeof(EffectCollection),
            typeof(PixelShader),
            typeof(BackdropBlurEffect),
            typeof(AcrylicEffect),
            typeof(MicaEffect),
            typeof(FrostedGlassEffect),
            typeof(ColorAdjustmentEffect),
            typeof(CompositeBackdropEffect),
        ];

        foreach (var expected in expectedTypes)
        {
            Assert.Same(expected, XamlTypeRegistry.GetType(expected.Name));
        }


#pragma warning disable CS0618
        Type[] legacyTypes =
        [
            typeof(BlurBitmapEffect),
            typeof(DropShadowBitmapEffect),
            typeof(BevelBitmapEffect),
            typeof(EmbossBitmapEffect),
            typeof(OuterGlowBitmapEffect),
            typeof(BitmapEffectGroup),
            typeof(BitmapEffectCollection),
            typeof(BitmapEffectInput),
        ];

        foreach (var expected in legacyTypes)
        {
            Assert.Same(expected, XamlTypeRegistry.GetType(expected.Name));
        }
#pragma warning restore CS0618
    }
}
