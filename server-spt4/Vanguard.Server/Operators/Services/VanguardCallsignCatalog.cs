using System.Security.Cryptography;
using System.Text;

// Responsibility: Owns the curated Vanguard callsign namespace and deterministic allocation policy.
// Flow: Contract generation and one-time identity migration both allocate from the same static, case-insensitive pool.
// Authority boundary: This catalog allocates labels only; persistent identity ownership remains with the Operator store/identity registry.
// Invariant: Callsigns are simple curated values, case-insensitively unique, and exhaustion fails closed without hash/GUID/numeric user-visible fallbacks.
namespace Vanguard.Server.Operators.Services;

internal static class VanguardCallsignCatalog
{
    static VanguardCallsignCatalog() => ValidateOrThrow();

    public const int MinimumRequiredPoolSize = 256;
    public const int CuratedPoolSize = 512;
    public const int HardPopulationSafetyCeilingPerOwner = 256;

    public static readonly string[] Values =
    [
        "Anchor", "Anvil", "Atlas", "Badger", "Bastion", "Beacon", "Bishop", "Bolt",
        "Breaker", "Briar", "Brim", "Cairn", "Cipher", "Cobalt", "Condor", "Coyote",
        "Crane", "Crow", "Dagger", "Delta", "Drift", "Echo", "Falcon", "Flint",
        "Forge", "Fox", "Frost", "Gale", "Ghost", "Grit", "Harrier", "Havoc",
        "Hawk", "Helix", "Hound", "Ibis", "Jackal", "Karat", "Kestrel", "Kodiak",
        "Lancer", "Lynx", "Mallet", "Mantis", "Mastiff", "Mica", "Nomad", "Onyx",
        "Orion", "Otter", "Palisade", "Pike", "Raven", "Razor", "Relay", "Rook",
        "Sable", "Scout", "Shade", "Slate", "Spear", "Stitch", "Stone", "Strix",
        "Talon", "Tango", "Thorn", "Titan", "Trace", "Vega", "Vector", "Viper",
        "Vostok", "Warden", "Wolf", "Wraith", "Yukon", "Zenith", "Aegis", "Arrow",
        "Ash", "Boreal", "Bronco", "Cedar", "Cliff", "Comet", "Crest", "Ember",
        "Fathom", "Gannet", "Hearth", "Keel", "Lumen", "Morrow", "North", "Quill",
        "Ridge", "Rivet", "Sierra", "Tern", "Timber", "Vale", "Whisper", "Zephyr",
        "Adder", "Albatross", "Antler", "Arctic", "Aspen", "Axe", "Badland", "Barracuda",
        "Basilisk", "Bayonet", "Beech", "Bison", "Blackbird", "Blizzard", "Boar", "Boulder",
        "Brand", "Buckler", "Buzzard", "Canyon", "Carbine", "Caribou", "Chisel", "Cobra",
        "Copper", "Corsair", "Cougar", "Crag", "Cricket", "Crossbow", "Cudgel", "Curlew",
        "Cyclone", "Dart", "Dingo", "Dune", "Eagle", "Elm", "Finch", "Fjord",
        "Flare", "Gator", "Granite", "Grouse", "Hammer", "Heron", "Hornet", "Iceberg",
        "Iron", "Jay", "Javelin", "Juniper", "Krait", "Lantern", "Leopard", "Lizard",
        "Mace", "Magpie", "Marble", "Merlin", "Moose", "Musket", "Nettle", "Osprey",
        "Panther", "Peak", "Pebble", "Peregrine", "Pine", "Piston", "Quake", "Quarry",
        "Ram", "Ranger", "Reef", "Repeater", "Saber", "Sandpiper", "Scorpion", "Shale",
        "Shrike", "Sleet", "Sniper", "Sparrow", "Spike", "Spruce", "Stallion", "Storm",
        "Tempest", "Tiger", "Tundra", "Vulture", "Walrus", "Wasp", "Willow", "Wolverine",
        "Acorn", "Aster", "Badlands", "Ballast", "Barrow", "Basalt", "Batten", "Beryl",
        "Birch", "Bivouac", "Bonfire", "Bramble", "Brant", "Buckeye", "Bullfinch", "Cactus",
        "Caliper", "Campion", "Capstan", "Chert", "Chough", "Clinker", "Coil", "Compass",
        "Corundum", "Cress", "Crucible", "Damper", "Dovetail", "Drill", "Duckbill", "Eider",
        "Escarpment", "Estuary", "Fir", "Firebrand", "Fletching", "Flintlock", "Gasket", "Goshawk",
        "Grackle", "Gyro", "Halyard", "Hatchet", "Hemlock", "Hopper", "Horseshoe", "Ingot",
        "Jasper", "Jerboa", "Kelp", "Knot", "Lagoon", "Lapwing", "Lathe", "Lemur",
        "Lichen", "Lobster", "Mako", "Mandrel", "Marl", "Minnow", "Mooring", "Mortar",
        "Mule", "Nautilus", "Obsidian", "Oryx", "Paddle", "Peridot", "Plover", "Porcupine",
        "Prowler", "Puffin", "Rake", "Ramrod", "Recurve", "Reedbuck", "Rigel", "Riptide",
        "Robin", "Rubble", "Saddle", "Scimitar", "Scree", "Sedge", "Shear", "Shovel",
        "Siskin", "Skate", "Sledge", "Snipe", "Socket", "Spanner", "Starling", "Stilt",
        "Strand", "Sundog", "Swift", "Tacker", "Tahr", "Alder", "Amethyst", "Anaconda",
        "Apricot", "Armadillo", "Auger", "Auk", "Avalanche", "Barbel", "Barnacle", "Bauxite",
        "Bellows", "Bittern", "Bobcat", "Borax", "Bracer", "Bream", "Bridle", "Bristle",
        "Buffel", "Caiman", "Caldera", "Camlock", "Candle", "Carp", "Cartouche", "Catapult",
        "Chamois", "Cinch", "Citrine", "Cleat", "Clove", "Cormorant", "Crank", "Cuttlefish",
        "Dace", "Darter", "Dolomite", "Dormer", "Dragonfly", "Dredge", "Egret", "Eland",
        "Emery", "Fennec", "Ferret", "Flail", "Flounder", "Garnet", "Gibbon", "Gimlet",
        "Glider", "Gneiss", "Grampus", "Grebe", "Halberd", "Halite", "Harpoon", "Hazel",
        "Ibex", "Iolite", "Jackdaw", "Jib", "Kudu", "Lapis", "Lark", "Limber",
        "Lime", "Mackerel", "Magnet", "Merganser", "Millstone", "Mongoose", "Muskrat", "Nighthawk",
        "Nuthatch", "Opal", "Oriel", "Oxbow", "Partridge", "Petrel", "Pickaxe", "Pipit",
        "Pumice", "Ratchet", "Sander", "Sapper", "Sardine", "Sawmill", "Sextant", "Sinker",
        "Skiff", "Snapper", "Solvent", "Stanchion", "Sturgeon", "Tongs", "Trestle", "Turret",
        "Umber", "Vise", "Whetstone", "Whimbrel", "Widgeon", "Agate", "Alidade", "Alligator",
        "Alum", "Amaranth", "Anser", "Antelope", "Arbalest", "Argon", "Armature", "Aurochs",
        "Awl", "Baboon", "Backstay", "Barite", "Batwing", "Beaver", "Bellbird", "Bitumen",
        "Bluebill", "Bobbin", "Bowline", "Bunker", "Burl", "Bustard", "Capybara", "Cassowary",
        "Catspaw", "Chalcite", "Churn", "Clam", "Cleaver", "Collet", "Conifer", "Cotter",
        "Crenel", "Dovekie", "Drizzle", "Drumlin", "Eelgrass", "Feldspar", "Fluke", "Foothill",
        "Fulcrum", "Gadwall", "Galena", "Gazelle", "Geode", "Gimbal", "Gopher", "Gouge",
        "Graver", "Greenfinch", "Gudgeon", "Guillemot", "Gunwale", "Haddock", "Hauberk", "Hawser",
        "Hickory", "Hoist", "Hoopoe", "Jackplane", "Kyanite", "Lamprey", "Lath", "Limonite",
        "Lintel", "Mainsail", "Mallard", "Manatee", "Manganese", "Marlin", "Marten", "Millrace",
        "Monsoon", "Narwhal", "Nipper", "Oakum", "Ocelot", "Ocher", "Oilstone", "Peavey",
        "Pelican", "Pincer", "Plumbline", "Quartz", "Quoin", "Rafter", "Rasp", "Redoubt",
        "Reamer", "Sarsen", "Scabbard", "Shackle", "Sheave", "Shoal", "Sirocco", "Skimmer",
        "Slag", "Sloop", "Snowcap", "Sprag", "Springbok", "Stag", "Tackle", "Tapir",
    ];

    public static bool TryPickUnique(Random random, HashSet<string> used, out string callsign)
    {
        callsign = string.Empty;
        if (Values.Length == 0) return false;

        int start = random.Next(Values.Length);
        for (int offset = 0; offset < Values.Length; offset++)
        {
            string candidate = Values[(start + offset) % Values.Length];
            if (used.Add(candidate))
            {
                callsign = candidate;
                return true;
            }
        }

        return false;
    }

    public static bool TryAllocateDeterministic(string stableKey, HashSet<string> used, out string callsign)
    {
        callsign = string.Empty;
        if (Values.Length == 0) return false;

        int start = StableIndex(stableKey, Values.Length);
        for (int offset = 0; offset < Values.Length; offset++)
        {
            string candidate = Values[(start + offset) % Values.Length];
            if (used.Add(candidate))
            {
                callsign = candidate;
                return true;
            }
        }

        return false;
    }

    public static bool IsSyntheticLegacyCallsign(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string candidate = value.Trim();
        if (!candidate.StartsWith("Echo-", StringComparison.OrdinalIgnoreCase)) return false;

        string suffix = candidate[5..];
        int dash = suffix.IndexOf('-');
        string hex = dash >= 0 ? suffix[..dash] : suffix;
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit)) return false;
        if (dash < 0) return true;

        string ordinal = suffix[(dash + 1)..];
        return ordinal.Length > 0 && ordinal.All(char.IsDigit);
    }

    public static void ValidateOrThrow()
    {
        if (Values.Length < MinimumRequiredPoolSize || Values.Length != CuratedPoolSize)
        {
            throw new InvalidOperationException($"Vanguard callsign catalog must contain exactly {CuratedPoolSize} entries and at least {MinimumRequiredPoolSize}.");
        }

        int distinct = Values.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (distinct != Values.Length)
        {
            throw new InvalidOperationException("Vanguard callsign catalog contains case-insensitive duplicates.");
        }

        if (Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Contains('-') || value.Any(char.IsDigit)))
        {
            throw new InvalidOperationException("Vanguard callsigns must be simple non-technical labels without hyphens or digits.");
        }
    }

    private static int StableIndex(string stableKey, int modulo)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(stableKey ?? string.Empty));
        uint value = BitConverter.ToUInt32(bytes, 0);
        return (int)(value % (uint)Math.Max(1, modulo));
    }
}
