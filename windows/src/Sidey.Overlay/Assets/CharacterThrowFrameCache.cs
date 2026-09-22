using System.Security.Cryptography;
using Sidey.Core.Domain;

namespace Sidey.Overlay.Assets;

internal sealed class CharacterThrowFrameCache : IDisposable
{
    internal const int ActionFrameCount = 8;
    internal const int ObjectFrameCount = 12;
    internal const int EmitterFrameCount = 4;

    private static readonly IReadOnlyDictionary<string, string> s_actionHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pixel_hamster"] = "b9915afdbb5476b17ea7b7f0a06eea09cc20b96dd1995328bdae2f806c3285c8",
            ["pixel_cat"] = "128e020aab718d8d45e81f599c221467b46f27aa809c45b1d873580cdaffcc62",
            ["pixel_puppy"] = "38d2858c97b456be17f34fd6b93df862e9c0d3b72e5592bb0e625e64110c5744",
            ["pixel_rabbit"] = "f641ebbba64e8c23d33173ff04d4907d3e15955024dd72e3f6e7d9b3e20fec84",
            ["pixel_penguin"] = "7ee5ea2b90994400a1b4dd252ed2affb095416597422a8ef4cb0ae54b3fb7f77",
            ["pixel_guinea_pig"] = "384157773baa55bd4a5f8586d179ab7eb43f42fb4204c306490784551e38ae1d",
            ["pixel_monkey"] = "059a288dde75695febec8a42303dc63f126636b094e3896b795b6a4ac1cce39a",
            ["pixel_chinchilla"] = "a6dd2b4f1837812bc9fd0d979fe379c4362ed8018b9d5e6991e5c28d53265b02",
            ["pixel_starlight_upalupa"] = "7a9bae8b1359f432857e026c972e3bc99777539ce7cfff89bc01e95d1938de75",
            ["pixel_otter"] = "ad7d076f4c63910c2c97f6e6ed03c27b47642a825d002d64b8c9dc86c11c2716",
            ["pixel_pig"] = "045344ec128ed34cfe2e6a641cf90c1a04b3ac639045dc200bc996c0089da85b",
            ["pixel_tree"] = "af22b750129b813d0affd8fa572604837f6bf67acfa14bda68074abf45a11f15",
            ["pixel_shiba"] = "0e2a54c5d53dd827526afca86ab3ec073f860da21ade1b1289b864c30f5a0e87",
            ["pixel_duck"] = "ad802bb86d51eed5e2e5a1208da93503ff30c3886c5e21e8e40b86494a78881f",
            ["pixel_poop"] = "2dfe350847ee18096f9e14431d573bf840f1445a4cef459622d77ca97760fc50",
            ["pixel_tteokbokki"] = "f0f71c0d3843662a7ba80faf8c6476257adda33547e325ceea2031885ff5c5fc",
            ["pixel_quokka"] = "ab43e2997d6e869ee6256a22a42f13ff76fc7b4f6a411f940319d7e98ab7a6f9",
        };

    private static readonly IReadOnlyDictionary<string, string> s_objectHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["patch_soft_ball"] = "cdde7f417c5d8d82d0f4df6b03fa8e7d494d98a37d75aa66699505d7c87c53fe",
            ["mini_paprika"] = "85b8d0525a865e531882a736561e9b7c4fbb6a2c3f80d91b456c4c4a7425724d",
            ["banana"] = "9cfca454ff6305fdd374c08f64c3c21e3af278166ffe15f7f81a183bb214f138",
            ["dust_bath_pouch"] = "b68022f5fe1a1a6a57fe56a01f73bae3d14b27d76f2dcbf10c6686b979634a65",
            ["starlight_orb"] = "08cf8ec8dc680ae07dcd83de9d56948873445470c6b15b5ad22e770f4277984c",
            ["throwable_bouncy_heart"] = "8474458c5d810a598c16a7f74bbfecf65300d7fb2c55aaaf0cabfa0399945305",
            ["throwable_toy_cannon"] = "c42c472f216ec4d291a41562dfaf6a28204625133961a5a225198daf87459bef",
            ["throwable_squeaky_duck"] = "3b6935398d41b6d1cd5efa922392dbf4864782deb9880c5d0f10885e00906e7a",
            ["clam"] = "561832b15538e3f57b0b6381e967130f21ba1a0aaf74110019588ff90da874e0",
            ["pork"] = "0a7acc60184321e05f7e0e04aed8972651331e50960aa18243b4958d27fd4da6",
            ["timber"] = "21280bb2ac9f9df4332552281fcba6fc1efdaf27b933eb058bf625a30221b63b",
            ["throwable_snowflake"] = "35ede7107f668d193c272773441215427da267e1f27b2beaab47d8c88ae37cd2",
            ["throwable_baseball"] = "73f3dc8d86a9f8f76f07494df5ee48a8ff5380feec55a718eed8aad6940b94e5",
            ["throwable_wakkuball"] = "4627c3538efcdaae3deda1ee390fbac92f96cf6e199dceb58a60f7917b7de295",
            ["throwable_dujjonku"] = "bb2468a5a9f3a692c294c7546c2b20cd4bfdfc903e6779304b2bfeccaea50b32",
            ["tennis_ball"] = "19c1a71275fd2e5be5be0b39ebd7be95d960f80ac65dd629aedd885b64f89fc3",
            ["tissue_ball"] = "ae0a3d483f6a606b4dc35bdac173044720668679f6462e36ff19720334dea975",
            ["fish_cake_skewer"] = "bdcfc04e33f3cf4f45305aa9fbf6c6fed70b515fe826823c890ccbb5e28340fd",
            ["leaf"] = "b0a419e6659ded829130ec7d0be92d0b693ccd1cdc158aa8aabbd8b24936173e",
        };

    private static readonly IReadOnlyDictionary<string, string> s_bgraHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Characters/pixel_hamster/throw_hit"] = "584b27d59d525c3d0e21d6ed9260a07cbba99f74069a4c95db601dd03cc3fb99",
            ["Characters/pixel_cat/throw_hit"] = "c5113a573980ae66ec3f93c05dac75120cdd30c30501ee90d1701876a6c574b7",
            ["Characters/pixel_puppy/throw_hit"] = "17b33fe94b2a76791b2d5002f720038ae1299d2185540866110e3258bb9b724a",
            ["Characters/pixel_rabbit/throw_hit"] = "5f715868aff1a2606c1f637f35ab1042d4b11f154ce91b5ddf21db33189b55c2",
            ["Characters/pixel_penguin/throw_hit"] = "658a54d43d8cd3c8a85b823a1dda11296addf45cad6ce58b7172128882666d0b",
            ["Characters/pixel_guinea_pig/throw_hit"] = "d9d96bb33233b9bbe0bf2e8effc6077bdfef978f4c3b61c00124fb8fc0715690",
            ["Characters/pixel_monkey/throw_hit"] = "b859c2e4bf6d890070c1ba085f47349771e84514e9ee2e50378da87ac7f0db6a",
            ["Characters/pixel_chinchilla/throw_hit"] = "b5bf003b3374a2d26f1c324c71ffbbe8acad54c19d9697861555edf0add0369c",
            ["Characters/pixel_starlight_upalupa/throw_hit"] = "3c27f23d45f14da7191850c4ec8c5a99d11d79bf415b0acd9445058e6c1db572",
            ["Throwables/patch_soft_ball/sprite"] = "64d4792b32df1c9dcae113d9a19dcee1f6fc5b6653a8dc4e80ffb1a2793f6e2f",
            ["Throwables/mini_paprika/sprite"] = "1e9f6181ef51c8e0f84f45f1dc12c14bf6141edb132c7131007e8937e969673f",
            ["Throwables/banana/sprite"] = "f42c588b897b5e33b3ca5f676dab15ce9e3aa3be02aad424c6b9ade8d01c372f",
            ["Throwables/dust_bath_pouch/sprite"] = "4d32c76073a8397379c33a42d9ee8e7656f6bf936af343a7d88e6c1d711d2205",
            ["Throwables/starlight_orb/sprite"] = "1719218f94d0686294b56fd836a74700e03312815043e050f0f0dbf4a90ba8ec",
            ["Throwables/throwable_bouncy_heart/sprite"] = "d1b5cd206fcdcccc91370dcb018375ca6e414d2893eee2f166bfdda055b7ca1c",
            ["Throwables/throwable_toy_cannon/sprite"] = "f685f7eaf078c1f800bbcd76525e717c7ebb72ae93f8c031fa2d4250b84969d4",
            ["Throwables/throwable_toy_cannon/emitter"] = "a7801effb2e7117ca7f2fc386c1f1e9bfddcf8aa70df3328312f573a84d890da",
            ["Throwables/throwable_squeaky_duck/sprite"] = "2456adbd1f17ea4b831b4d58bb510c3a9a663a7aa2a36c030f286ab4e9ee31f9",
            ["Characters/pixel_otter/throw_hit"] = "27aeb7a143f9bbeda7d5aab4dc0c69666d444e5e09f27b9249d3c22b4d960e4f",
            ["Characters/pixel_pig/throw_hit"] = "a4da4e142417243e16c8e60f9ba7864d836d3fbd3c88aa607dfa6dd56e9d8b5b",
            ["Characters/pixel_tree/throw_hit"] = "4bc7a7960f778346910a5e071b0396a3a2d61ca869fd79eec58d6516d2c71c85",
            ["Characters/pixel_shiba/throw_hit"] = "3640fbb89e5f45087458e1c2ba428fa35c62eddfbd2be5a396ac4180f0756548",
            ["Characters/pixel_duck/throw_hit"] = "b29aa940f3094a22e3553791a454913716f1c25e9962c718258d4cd3cb08cf93",
            ["Characters/pixel_poop/throw_hit"] = "1a83d67282a48c2b432656b4843524a10bfffec3d47d3390089db236ec317dba",
            ["Characters/pixel_tteokbokki/throw_hit"] = "cf513ed3cc6d581385c98e1f6f3f4e2403b3643ef02e5f64314138e62b642b8d",
            ["Characters/pixel_quokka/throw_hit"] = "d7c7779507c689026adb0a77b8a2d6ca61fdafe91ca2f37a3bb11b02b0b88398",
            ["Throwables/clam/sprite"] = "fb5486a6a17ac8406ccf63c9671de233f62d285376f598361be8c02edc23d8c4",
            ["Throwables/pork/sprite"] = "f521506a1d95f1423dbade5645c4a2e9497878bdab672729f1ff8169c059ff3f",
            ["Throwables/timber/sprite"] = "bcc35b32478d06fa051a306ecf14475d1429d75b9cf58e8949de28773b566a8d",
            ["Throwables/throwable_snowflake/sprite"] = "d7c5a1438ba9d9cc65684fab0c484ddb467c2e7bc27a85de90a050c124246483",
            ["Throwables/throwable_baseball/sprite"] = "c0df6dd6c04815fa4373eb6dbb710a8f36db0e120f7c07d81206f9259f3f1e6c",
            ["Throwables/throwable_wakkuball/sprite"] = "5bed22d92763fb0a16917ef89b03d86b94e63e3594f52021437cc861582de1b0",
            ["Throwables/throwable_dujjonku/sprite"] = "f1b7c6583f64a6990c556a29d8874a8762b61ecf9117c79f7fe86a73f8423e53",
            ["Throwables/tennis_ball/sprite"] = "3c4c13ab951ce13d138e94233a3d954dffdbb35aef53c8547978183a9567b418",
            ["Throwables/tissue_ball/sprite"] = "30aeb15b7ffc56982ed3af71eb7908469949dc4c7b80b18f089b5653d940d65f",
            ["Throwables/fish_cake_skewer/sprite"] = "f40f0ab9d981835201372015a3bc3961892bcc037edd7cda3937f71acd3664ca",
            ["Throwables/leaf/sprite"] = "b16c4c00835d7eaac04307983271ce931207acabbb89e8d3d688f4f44b59f137",
        };

    private readonly Dictionary<string, byte[][]> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[][]> _flippedActions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[][]> _objects = new(StringComparer.Ordinal);
    private readonly byte[][] _cannonEmitters;
    private readonly byte[][] _flippedCannonEmitters;
    private bool _disposed;

    internal CharacterThrowFrameCache(
        string characterRoot,
        string throwableRoot,
        int scale,
        OverlayEdge edge)
    {
        foreach (string characterId in s_actionHashes.Keys)
        {
            (byte[][]? normal, byte[][]? flipped) = LoadAction(
                characterRoot,
                characterId,
                s_actionHashes[characterId],
                scale,
                edge);
            _actions.Add(characterId, normal);
            _flippedActions.Add(characterId, flipped);
        }
        foreach (string objectId in s_objectHashes.Keys)
        {
            _objects.Add(objectId, LoadSheet(
                Path.Combine(throwableRoot, objectId),
                "sprite",
                $"Throwables/{objectId}/sprite",
                s_objectHashes[objectId],
                cellSize: 16,
                frameCount: ObjectFrameCount,
                scale: scale,
                flip: false,
                edge));
        }
        _cannonEmitters = LoadSheet(
            Path.Combine(throwableRoot, "throwable_toy_cannon"),
            "emitter",
            "Throwables/throwable_toy_cannon/emitter",
            "7869a47c2f72a17894646e2f63eab1166b42d13f2a72fa83311075b3d809ffbf",
            cellSize: 24,
            frameCount: EmitterFrameCount,
            scale: scale,
            flip: false,
            edge);
        _flippedCannonEmitters = LoadSheet(
            Path.Combine(throwableRoot, "throwable_toy_cannon"),
            "emitter",
            "Throwables/throwable_toy_cannon/emitter",
            "7869a47c2f72a17894646e2f63eab1166b42d13f2a72fa83311075b3d809ffbf",
            cellSize: 24,
            frameCount: EmitterFrameCount,
            scale: scale,
            flip: true,
            edge);
    }

    internal int ObjectPixelSize { get; private set; }

    internal ReadOnlySpan<byte> ActionFrame(string? characterId, int frame, bool flipped)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string id = PixelCharacterCatalog.NormalizeId(characterId);
        byte[][] frames = flipped ? _flippedActions[id] : _actions[id];
        return frames[Math.Clamp(frame, 0, ActionFrameCount - 1)];
    }

    internal ReadOnlySpan<byte> ObjectFrame(string? sourceCharacterId, string? throwableId, int frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string objectId = CosmeticCatalog.ResolveThrowableAssetId(throwableId);
        return _objects[objectId][Math.Clamp(frame, 0, ObjectFrameCount - 1)];
    }

    internal ReadOnlySpan<byte> ObjectFrame(string? sourceCharacterId, int frame) =>
        ObjectFrame(sourceCharacterId, throwableId: null, frame);

    internal ReadOnlySpan<byte> CannonEmitterFrame(int frame, bool flipped)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[][] frames = flipped ? _flippedCannonEmitters : _cannonEmitters;
        return frames[Math.Clamp(frame, 0, EmitterFrameCount - 1)];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (byte[]? frame in _actions.Values.SelectMany(value => value)
                     .Concat(_flippedActions.Values.SelectMany(value => value))
                     .Concat(_objects.Values.SelectMany(value => value))
                     .Concat(_cannonEmitters)
                     .Concat(_flippedCannonEmitters))
        {
            Array.Clear(frame);
        }
        _actions.Clear();
        _flippedActions.Clear();
        _objects.Clear();
    }

    private (byte[][] Normal, byte[][] Flipped) LoadAction(
        string characterRoot,
        string characterId,
        string hash,
        int scale,
        OverlayEdge edge)
    {
        byte[][] normal = LoadSheet(
            Path.Combine(characterRoot, characterId),
            "throw_hit",
            $"Characters/{characterId}/throw_hit",
            hash,
            cellSize: 24,
            frameCount: ActionFrameCount,
            scale: scale,
            flip: false,
            edge);
        byte[][] flipped = LoadSheet(
            Path.Combine(characterRoot, characterId),
            "throw_hit",
            $"Characters/{characterId}/throw_hit",
            hash,
            cellSize: 24,
            frameCount: ActionFrameCount,
            scale: scale,
            flip: true,
            edge);
        return (normal, flipped);
    }

    private byte[][] LoadSheet(
        string directory,
        string name,
        string resourceId,
        string expectedPngHash,
        int cellSize,
        int frameCount,
        int scale,
        bool flip,
        OverlayEdge edge)
    {
        byte[] png = File.ReadAllBytes(Path.Combine(directory, name + ".png"));
        if (!StringComparer.Ordinal.Equals(
                Convert.ToHexStringLower(SHA256.HashData(png)),
                expectedPngHash))
        {
            throw new InvalidDataException($"{name} throw asset hash does not match the approved manifest.");
        }

        byte[] sheet = File.ReadAllBytes(Path.Combine(directory, name + ".bgra"));
        int expectedLength = checked(cellSize * frameCount * cellSize * 4);
        if (sheet.Length != expectedLength)
        {
            throw new InvalidDataException($"{name} BGRA sheet has an invalid byte length.");
        }
        if (!s_bgraHashes.TryGetValue(resourceId, out string? expectedBgraHash)
            || !StringComparer.Ordinal.Equals(
                Convert.ToHexStringLower(SHA256.HashData(sheet)),
                expectedBgraHash))
        {
            throw new InvalidDataException($"{name} BGRA sheet hash does not match the Windows cache manifest.");
        }

        byte[][] frames = new byte[frameCount][];
        for (int frame = 0; frame < frameCount; frame++)
        {
            frames[frame] = BuildFrame(sheet, cellSize, frameCount, frame, scale, flip, edge);
        }
        if (cellSize == 16)
        {
            ObjectPixelSize = cellSize * scale;
        }
        Array.Clear(sheet);
        return frames;
    }

    private static byte[] BuildFrame(
        ReadOnlySpan<byte> sheet,
        int cellSize,
        int frameCount,
        int frame,
        int scale,
        bool flip,
        OverlayEdge edge)
    {
        int sheetWidth = cellSize * frameCount;
        int outputSize = cellSize * scale;
        byte[] output = new byte[outputSize * outputSize * 4];
        for (int y = 0; y < outputSize; y++)
        {
            for (int x = 0; x < outputSize; x++)
            {
                int rotatedX = x / scale;
                int rotatedY = y / scale;
                (int sourceX, int sourceY) = InverseRotate(rotatedX, rotatedY, cellSize, edge);
                if (flip)
                {
                    sourceX = cellSize - 1 - sourceX;
                }
                // Throw, hit, projectile, and emitter BGRA mirrors are stored bottom-up.
                // Convert the authored top-down coordinate after edge rotation so action
                // frames keep the same orientation as the base character frames.
                int storedSourceY = cellSize - 1 - sourceY;
                int input = ((storedSourceY * sheetWidth) + (frame * cellSize) + sourceX) * 4;
                int destination = ((y * outputSize) + x) * 4;
                sheet.Slice(input, 4).CopyTo(output.AsSpan(destination, 4));
            }
        }
        return output;
    }

    private static (int X, int Y) InverseRotate(int x, int y, int size, OverlayEdge edge) =>
        edge switch
        {
            OverlayEdge.Bottom => (x, y),
            OverlayEdge.Top => (size - 1 - x, size - 1 - y),
            OverlayEdge.Left => (y, size - 1 - x),
            OverlayEdge.Right => (size - 1 - y, x),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
}
