namespace NeoAdmin;

internal sealed record AdminGiveItem(
    string Name,
    string EntityClass);

internal sealed record AdminGiveItemCategory(
    string Name,
    IReadOnlyList<AdminGiveItem> Items);

internal static class AdminGiveItemCatalog
{
    public static IReadOnlyList<AdminGiveItemCategory> Categories { get; } =
    [
        new("Pistols",
        [
            new("CZ75-Auto", "weapon_cz75a"),
            new("Desert Eagle", "weapon_deagle"),
            new("Dual Berettas", "weapon_elite"),
            new("Five-SeveN", "weapon_fiveseven"),
            new("Glock-18", "weapon_glock"),
            new("P2000", "weapon_hkp2000"),
            new("P250", "weapon_p250"),
            new("R8 Revolver", "weapon_revolver"),
            new("Tec-9", "weapon_tec9"),
            new("USP-S", "weapon_usp_silencer"),
        ]),
        new("SMGs",
        [
            new("MAC-10", "weapon_mac10"),
            new("MP5-SD", "weapon_mp5sd"),
            new("MP7", "weapon_mp7"),
            new("MP9", "weapon_mp9"),
            new("P90", "weapon_p90"),
            new("PP-Bizon", "weapon_bizon"),
            new("UMP-45", "weapon_ump45"),
        ]),
        new("Rifles",
        [
            new("AK-47", "weapon_ak47"),
            new("AUG", "weapon_aug"),
            new("AWP", "weapon_awp"),
            new("FAMAS", "weapon_famas"),
            new("G3SG1", "weapon_g3sg1"),
            new("Galil AR", "weapon_galilar"),
            new("M4A1-S", "weapon_m4a1_silencer"),
            new("M4A4", "weapon_m4a1"),
            new("SCAR-20", "weapon_scar20"),
            new("SG 553", "weapon_sg556"),
            new("SSG 08", "weapon_ssg08"),
        ]),
        new("Heavy",
        [
            new("M249", "weapon_m249"),
            new("MAG-7", "weapon_mag7"),
            new("Negev", "weapon_negev"),
            new("Nova", "weapon_nova"),
            new("Sawed-Off", "weapon_sawedoff"),
            new("XM1014", "weapon_xm1014"),
        ]),
        new("Grenades",
        [
            new("Decoy Grenade", "weapon_decoy"),
            new("Flashbang", "weapon_flashbang"),
            new("HE Grenade", "weapon_hegrenade"),
            new("Incendiary Grenade", "weapon_incgrenade"),
            new("Molotov", "weapon_molotov"),
            new("Smoke Grenade", "weapon_smokegrenade"),
        ]),
        new("Equipment",
        [
            new("Defuse Kit", "item_defuser"),
            new("Kevlar Vest", "item_kevlar"),
            new("Kevlar + Helmet", "item_assaultsuit"),
            new("Zeus x27", "weapon_taser"),
        ]),
    ];
}
