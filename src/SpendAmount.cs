namespace MonsterIntentRandomizer;

internal static class SpendAmount
{
    internal static int Closest(int minimum, int maximum, double target, double limit,
        Func<int, double> price, bool descending = false)
    {
        if (maximum < minimum || limit <= 0)
            return 0;
        target = Math.Min(target, limit);

        // An existing threshold can leave only a few possible improvements.
        if (maximum - minimum <= 16)
            return Best(Enumerable.Range(minimum, maximum - minimum + 1));

        int pivot = Boundary(descending ? amount => price(amount) <= target : amount => price(amount) >= Math.Max(target, double.Epsilon));
        int chosen = Best(pivot > minimum ? [pivot - 1, pivot] : [pivot]);
        if (chosen != 0 && !descending)
        {
            // Use the least amount with this price when the formula has a plateau.
            double selectedPrice = price(chosen);
            int first = Boundary(amount => price(amount) >= selectedPrice);
            if (price(first) == selectedPrice)
                return first;
        }
        return chosen;

        int Boundary(Func<int, bool> condition)
        {
            int low = minimum, high = maximum;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (condition(middle))
                    high = middle;
                else
                    low = middle + 1;
            }
            return low;
        }

        int Best(IEnumerable<int> amounts)
        {
            int best = 0;
            double distance = double.PositiveInfinity;
            foreach (int amount in amounts)
            {
                double cost = price(amount);
                if (!(cost > 0 && cost <= limit + 1e-9))
                    continue;
                double difference = Math.Abs(cost - target);
                if (difference < distance)
                {
                    best = amount;
                    distance = difference;
                }
            }
            return best;
        }
    }
}
