using System;
using System.Collections.Generic;

public class CircularDependencyDetector
{
    private enum NodeColor
    {
        White, // Unvisited
        Grey,  // Currently in the active recursion stack (Exploring ancestors)
        Black  // Fully processed (Safe, verified cycle-free from this node onwards)
    }

    public static List<List<string>> DetectCycles(Dictionary<string, List<string>> adjacencyList)
    {
        var cycles = new List<List<string>>();
        var colors = new Dictionary<string, NodeColor>(StringComparer.OrdinalIgnoreCase);
        var currentPath = new List<string>();

        // Initialize all nodes to White (Unvisited)
        foreach (var node in adjacencyList.Keys)
        {
            colors[node] = NodeColor.White;
        }

        // Loop through all nodes to handle disconnected graph components safely
        foreach (var node in adjacencyList.Keys)
        {
            if (colors[node] == NodeColor.White)
            {
                DFS(node, adjacencyList, colors, currentPath, cycles);
            }
        }

        return cycles;
    }

    private static void DFS(
        string u,
        Dictionary<string, List<string>> adjList,
        Dictionary<string, NodeColor> colors,
        List<string> currentPath,
        List<List<string>> cycles)
    {
        // Move node to Grey status and push onto the recursion stack
        colors[u] = NodeColor.Grey;
        currentPath.Add(u);

        // Safely pull outgoing dependency edges (if node has no dependencies, initialize empty list)
        if (adjList.TryGetValue(u, out var neighbors))
        {
            foreach (var v in neighbors)
            {
                if (!colors.TryGetValue(v, out var neighborColor))
                {
                    neighborColor = NodeColor.White;
                }

                if (neighborColor == NodeColor.Grey)
                {
                    // CYCLE DETECTED! 'v' is currently in our ancestry tree/recursion stack.
                    // Backtrack through currentPath to capture the exact loop sequence
                    int cycleStartIndex = currentPath.IndexOf(v);
                    if (cycleStartIndex != -1)
                    {
                        var cyclePath = new List<string>();
                        for (int i = cycleStartIndex; i < currentPath.Count; i++)
                        {
                            cyclePath.Add(currentPath[i]);
                        }
                        cyclePath.Add(v); // Append target node to close the loop path cleanly
                        cycles.Add(cyclePath);
                    }
                }
                else if (neighborColor == NodeColor.White)
                {
                    // Continue exploring deeper down the current branch
                    DFS(v, adjList, colors, currentPath, cycles);
                }
                // If Black, it has already been completely verified, skip it to save cycles.
            }
        }

        // Backtracking phase: Remove from recursion stack and mark as Black (safe)
        currentPath.RemoveAt(currentPath.Count - 1);
        colors[u] = NodeColor.Black;
    }
}