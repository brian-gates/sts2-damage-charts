namespace STS2_DamageCharts;

// Pure math for OverlayMount (no Godot refs, so it is unit-tested): the holder transform that cancels a
// parent's scale+offset so content placed in the holder keeps viewport coordinates.
internal static class OverlayTransform
{
    // The parent maps local p to viewport s*p + o (per axis). The holder's own transform T(pos)*S(scale)
    // must be that map's inverse: scale 1/s and position -o/s. Rotation/skew are not modeled (UI parents
    // don't rotate). A zero or non-finite scale can't be inverted, so it yields the identity.
    public static (float PosX, float PosY, float ScaleX, float ScaleY) Inverse(float scaleX, float scaleY, float originX, float originY)
    {
        if (!Invertible(scaleX) || !Invertible(scaleY) || !float.IsFinite(originX) || !float.IsFinite(originY))
            return (0f, 0f, 1f, 1f);
        return (-originX / scaleX, -originY / scaleY, 1f / scaleX, 1f / scaleY);
    }

    private static bool Invertible(float s) => float.IsFinite(s) && System.Math.Abs(s) > 1e-6f;

    // Where a mount holder of `rank` belongs among its siblings: in the run of mount holders directly before
    // the hover-tip node (`tips`), above every holder of lower rank and below every holder of higher rank.
    // `rankAt(i)` is the rank of sibling i when it is a mount holder, else null. Returns the index to pass
    // to MoveChild, or -1 when `self` is already in place, so a settled tree is never re-sorted.
    public static int PlaceIndex(int self, int tips, int rank, System.Func<int, int?> rankAt)
    {
        if (tips < 0) return -1;
        if (self >= 0 && self < tips)
        {
            bool inPlace = true;
            for (int i = self + 1; i < tips && inPlace; i++)
                inPlace = rankAt(i) is int r && r >= rank;
            if (inPlace && self > 0 && rankAt(self - 1) is int prev && prev > rank) inPlace = false;
            if (inPlace) return -1;
        }
        int higher = 0;
        for (int i = tips - 1; i >= 0; i--)
        {
            if (i == self) continue;
            if (rankAt(i) is not int r) break;
            if (r > rank) higher++;
        }
        int tipsAfterRemoval = self >= 0 && self < tips ? tips - 1 : tips;
        return tipsAfterRemoval - higher;
    }
}
