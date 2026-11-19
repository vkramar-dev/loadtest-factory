namespace LoadTestFactory.Utilities;

internal static class MathEx
{
    private static int GetPairGcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    internal static int GetGcd(int[] weights)
    {
        int gcd = weights[0];

        for (int i = 1; i < weights.Length; i++)
        {
            gcd = GetPairGcd(gcd, weights[i]);

            if (gcd == 1)
                break;
        }

        return gcd;
    }
}
