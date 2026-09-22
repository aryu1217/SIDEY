using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Sidey.Core.Domain;
using Sidey.Infrastructure;
using Sidey.Overlay;

namespace Sidey.Platform.Windows.Tests;

public sealed class CharacterThrowAssetTests
{
    private static readonly IReadOnlyDictionary<string, string> s_pngHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Characters/pixel_hamster/throw_hit"] = "b9915afdbb5476b17ea7b7f0a06eea09cc20b96dd1995328bdae2f806c3285c8",
            ["Characters/pixel_cat/throw_hit"] = "128e020aab718d8d45e81f599c221467b46f27aa809c45b1d873580cdaffcc62",
            ["Characters/pixel_puppy/throw_hit"] = "38d2858c97b456be17f34fd6b93df862e9c0d3b72e5592bb0e625e64110c5744",
            ["Characters/pixel_rabbit/throw_hit"] = "f641ebbba64e8c23d33173ff04d4907d3e15955024dd72e3f6e7d9b3e20fec84",
            ["Characters/pixel_penguin/throw_hit"] = "7ee5ea2b90994400a1b4dd252ed2affb095416597422a8ef4cb0ae54b3fb7f77",
            ["Characters/pixel_guinea_pig/throw_hit"] = "384157773baa55bd4a5f8586d179ab7eb43f42fb4204c306490784551e38ae1d",
            ["Characters/pixel_monkey/throw_hit"] = "059a288dde75695febec8a42303dc63f126636b094e3896b795b6a4ac1cce39a",
            ["Characters/pixel_chinchilla/throw_hit"] = "a6dd2b4f1837812bc9fd0d979fe379c4362ed8018b9d5e6991e5c28d53265b02",
            ["Characters/pixel_starlight_upalupa/throw_hit"] = "7a9bae8b1359f432857e026c972e3bc99777539ce7cfff89bc01e95d1938de75",
            ["Characters/pixel_shiba/throw_hit"] = "0e2a54c5d53dd827526afca86ab3ec073f860da21ade1b1289b864c30f5a0e87",
            ["Characters/pixel_duck/throw_hit"] = "ad802bb86d51eed5e2e5a1208da93503ff30c3886c5e21e8e40b86494a78881f",
            ["Characters/pixel_poop/throw_hit"] = "2dfe350847ee18096f9e14431d573bf840f1445a4cef459622d77ca97760fc50",
            ["Characters/pixel_tteokbokki/throw_hit"] = "f0f71c0d3843662a7ba80faf8c6476257adda33547e325ceea2031885ff5c5fc",
            ["Characters/pixel_quokka/throw_hit"] = "ab43e2997d6e869ee6256a22a42f13ff76fc7b4f6a411f940319d7e98ab7a6f9",
            ["Throwables/patch_soft_ball/sprite"] = "cdde7f417c5d8d82d0f4df6b03fa8e7d494d98a37d75aa66699505d7c87c53fe",
            ["Throwables/mini_paprika/sprite"] = "85b8d0525a865e531882a736561e9b7c4fbb6a2c3f80d91b456c4c4a7425724d",
            ["Throwables/banana/sprite"] = "9cfca454ff6305fdd374c08f64c3c21e3af278166ffe15f7f81a183bb214f138",
            ["Throwables/dust_bath_pouch/sprite"] = "b68022f5fe1a1a6a57fe56a01f73bae3d14b27d76f2dcbf10c6686b979634a65",
            ["Throwables/starlight_orb/sprite"] = "08cf8ec8dc680ae07dcd83de9d56948873445470c6b15b5ad22e770f4277984c",
            ["Throwables/throwable_bouncy_heart/sprite"] = "8474458c5d810a598c16a7f74bbfecf65300d7fb2c55aaaf0cabfa0399945305",
            ["Throwables/throwable_toy_cannon/sprite"] = "c42c472f216ec4d291a41562dfaf6a28204625133961a5a225198daf87459bef",
            ["Throwables/throwable_squeaky_duck/sprite"] = "3b6935398d41b6d1cd5efa922392dbf4864782deb9880c5d0f10885e00906e7a",
            ["Throwables/tennis_ball/sprite"] = "19c1a71275fd2e5be5be0b39ebd7be95d960f80ac65dd629aedd885b64f89fc3",
            ["Throwables/tissue_ball/sprite"] = "ae0a3d483f6a606b4dc35bdac173044720668679f6462e36ff19720334dea975",
            ["Throwables/fish_cake_skewer/sprite"] = "bdcfc04e33f3cf4f45305aa9fbf6c6fed70b515fe826823c890ccbb5e28340fd",
            ["Throwables/leaf/sprite"] = "b0a419e6659ded829130ec7d0be92d0b693ccd1cdc158aa8aabbd8b24936173e",
        };

    private static readonly IReadOnlyDictionary<string, string> s_cosmeticBgraHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Throwables/throwable_bouncy_heart/sprite"] = "d1b5cd206fcdcccc91370dcb018375ca6e414d2893eee2f166bfdda055b7ca1c",
            ["Throwables/throwable_toy_cannon/sprite"] = "f685f7eaf078c1f800bbcd76525e717c7ebb72ae93f8c031fa2d4250b84969d4",
            ["Throwables/throwable_toy_cannon/emitter"] = "a7801effb2e7117ca7f2fc386c1f1e9bfddcf8aa70df3328312f573a84d890da",
            ["Throwables/throwable_squeaky_duck/sprite"] = "2456adbd1f17ea4b831b4d58bb510c3a9a663a7aa2a36c030f286ab4e9ee31f9",
            ["Characters/pixel_shiba/throw_hit"] = "3640fbb89e5f45087458e1c2ba428fa35c62eddfbd2be5a396ac4180f0756548",
            ["Characters/pixel_duck/throw_hit"] = "b29aa940f3094a22e3553791a454913716f1c25e9962c718258d4cd3cb08cf93",
            ["Characters/pixel_poop/throw_hit"] = "1a83d67282a48c2b432656b4843524a10bfffec3d47d3390089db236ec317dba",
            ["Characters/pixel_tteokbokki/throw_hit"] = "cf513ed3cc6d581385c98e1f6f3f4e2403b3643ef02e5f64314138e62b642b8d",
            ["Characters/pixel_quokka/throw_hit"] = "d7c7779507c689026adb0a77b8a2d6ca61fdafe91ca2f37a3bb11b02b0b88398",
            ["Throwables/tennis_ball/sprite"] = "3c4c13ab951ce13d138e94233a3d954dffdbb35aef53c8547978183a9567b418",
            ["Throwables/tissue_ball/sprite"] = "30aeb15b7ffc56982ed3af71eb7908469949dc4c7b80b18f089b5653d940d65f",
            ["Throwables/fish_cake_skewer/sprite"] = "f40f0ab9d981835201372015a3bc3961892bcc037edd7cda3937f71acd3664ca",
            ["Throwables/leaf/sprite"] = "b16c4c00835d7eaac04307983271ce931207acabbb89e8d3d688f4f44b59f137",
        };

    [Fact]
    public void ApprovedPngsAndPreconvertedBgraSheetsMatchTheContract()
    {
        foreach (KeyValuePair<string, string> pair in s_pngHashes)
        {
            byte[] png = File.ReadAllBytes(AssetPath(pair.Key + ".png"));
            Assert.Equal(pair.Value, Convert.ToHexStringLower(SHA256.HashData(png)));
            Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
            int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
            int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
            Assert.Equal(192, width);
            Assert.Equal(pair.Key.StartsWith("Characters/", StringComparison.Ordinal) ? 24 : 16, height);
            Assert.Equal(width * height * 4, File.ReadAllBytes(AssetPath(pair.Key + ".bgra")).Length);
        }
        foreach (KeyValuePair<string, string> pair in s_cosmeticBgraHashes)
        {
            byte[] bgra = File.ReadAllBytes(AssetPath(pair.Key + ".bgra"));
            Assert.Equal(pair.Value, Convert.ToHexStringLower(SHA256.HashData(bgra)));
        }
    }

    [Fact]
    public void CharacterActionsRemainDistinctAndUnequippedCharactersUseTheCommonBall()
    {
        using var cache = new CharacterThrowFrameCache(
            Path.Combine(AppContext.BaseDirectory, "Assets", "Characters"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "Throwables"),
            scale: 1,
            edge: OverlayEdge.Bottom);

        byte[] hamsterAction = cache.ActionFrame("pixel_hamster", frame: 0, flipped: false).ToArray();
        Assert.False(hamsterAction.SequenceEqual(cache.ActionFrame("pixel_guinea_pig", frame: 0, flipped: false).ToArray()));
        Assert.False(hamsterAction.SequenceEqual(cache.ActionFrame("pixel_monkey", frame: 0, flipped: false).ToArray()));
        Assert.False(hamsterAction.SequenceEqual(cache.ActionFrame("pixel_chinchilla", frame: 0, flipped: false).ToArray()));
        Assert.False(hamsterAction.SequenceEqual(cache.ActionFrame("pixel_starlight_upalupa", frame: 0, flipped: false).ToArray()));
        Assert.Equal(hamsterAction, cache.ActionFrame("unknown_character", frame: 0, flipped: false).ToArray());

        byte[] patchBall = cache.ObjectFrame("pixel_hamster", frame: 0).ToArray();
        Assert.Equal(patchBall, cache.ObjectFrame("pixel_guinea_pig", frame: 0).ToArray());
        Assert.Equal(patchBall, cache.ObjectFrame("pixel_monkey", frame: 0).ToArray());
        Assert.Equal(patchBall, cache.ObjectFrame("pixel_chinchilla", frame: 0).ToArray());
        Assert.Equal(patchBall, cache.ObjectFrame("pixel_starlight_upalupa", frame: 0).ToArray());
        Assert.Equal(patchBall, cache.ObjectFrame("unknown_character", frame: 0).ToArray());
        Assert.False(patchBall.SequenceEqual(cache.ObjectFrame(
            "pixel_hamster", "throwable_bouncy_heart", frame: 0).ToArray()));
        Assert.False(patchBall.SequenceEqual(cache.ObjectFrame(
            "pixel_hamster", "throwable_toy_cannon", frame: 0).ToArray()));
        Assert.False(patchBall.SequenceEqual(cache.ObjectFrame(
            "pixel_hamster", "throwable_squeaky_duck", frame: 0).ToArray()));
        foreach (CommerceProduct product in WindowsCommerceCatalog.Products.Where(product =>
                     product.Kind == CommerceProductKind.Throwable))
        {
            byte[] equipped = cache.ObjectFrame("pixel_hamster", product.EffectiveCatalogItemId, 0).ToArray();
            Assert.NotEmpty(equipped);
            Assert.False(patchBall.SequenceEqual(equipped));
            Assert.Equal(equipped, cache.ObjectFrame("pixel_tree", product.EffectiveCatalogItemId, 0).ToArray());
            foreach (string id in new[] { product.EffectiveCatalogItemId, product.RenderAssetId! })
            {
                using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { throwable_id = id }));
                string? receivedId = SupabaseRealtimeTransport.ParseThrowableAssetId(payload.RootElement);
                Assert.Equal(product.RenderAssetId, receivedId);
                Assert.Equal(equipped, cache.ObjectFrame("pixel_hamster", receivedId, 0).ToArray());
            }
        }
        Assert.NotEmpty(cache.CannonEmitterFrame(frame: 0, flipped: false).ToArray());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"throwable_id\":null}")]
    [InlineData("{\"throwable_id\":42}")]
    [InlineData("{\"throwable_id\":\"unknown\"}")]
    [InlineData("{\"throwable_id\":\"throwable_unknown\"}")]
    [InlineData("{\"throwable_id\":\"../pork\"}")]
    [InlineData("{\"throwable_id\":\"patch_soft_ball\"}")]
    public void MissingOrUnknownBroadcastThrowablesUseTheCommonBall(string json)
    {
        using var payload = JsonDocument.Parse(json);
        string? receivedId = SupabaseRealtimeTransport.ParseThrowableAssetId(payload.RootElement);
        Assert.Equal("patch_soft_ball", CosmeticCatalog.ResolveThrowableAssetId(receivedId));
    }

    [Fact]
    public void BottomEdgeActionFramesConvertTheBottomUpBgraMirrorToTopDownPixels()
    {
        using var cache = new CharacterThrowFrameCache(
            Path.Combine(AppContext.BaseDirectory, "Assets", "Characters"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "Throwables"),
            scale: 1,
            edge: OverlayEdge.Bottom);
        byte[] sheet = File.ReadAllBytes(AssetPath("Characters/pixel_hamster/throw_hit.bgra"));
        byte[] expected = new byte[24 * 24 * 4];
        const int SheetRowBytes = 192 * 4;
        const int FrameRowBytes = 24 * 4;
        for (int y = 0; y < 24; y++)
        {
            sheet.AsSpan((23 - y) * SheetRowBytes, FrameRowBytes)
                .CopyTo(expected.AsSpan(y * FrameRowBytes, FrameRowBytes));
        }

        Assert.Equal(expected, cache.ActionFrame("pixel_hamster", frame: 0, flipped: false).ToArray());
    }

    private static string AssetPath(string relative) => Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        relative.Replace('/', Path.DirectorySeparatorChar));
}
