using System;
using System.Collections.Generic;

namespace UColliders.CoACD
{
    /// <summary>
    /// Monte Carlo Tree Search for finding optimal axis-aligned cutting planes.
    /// Uses a minimization variant of UCB1 where lower concavity is better.
    /// </summary>
    internal class MCTSearch
    {
        /// <summary>
        /// One mesh piece tracked in the MCTS state.
        /// </summary>
        class Part
        {
            public CoACDMesh mesh;
            public CoACDMesh hull;
            public double rv;
            public List<CoACDPlane> availableMoves;
            public int nextChoice;
        }

        /// <summary>
        /// Full MCTS game state at a tree node.
        /// </summary>
        class State
        {
            public List<Part> parts;
            public int worstPartIdx;
            public double currentCost;
            public int currentRound;
            public CoACDPlane actionTaken;
            public int actionPartIdx;
            public double terminalThreshold;
            public int maxDepth;
            public int mctsNodes;

            public bool IsTerminal()
            {
                if (currentRound >= maxDepth) return true;
                if (worstPartIdx < 0 || worstPartIdx >= parts.Count) return true;
                if (parts[worstPartIdx].availableMoves == null) return true;
                return parts[worstPartIdx].availableMoves.Count == 0;
            }

            public bool IsAllExpanded()
            {
                if (worstPartIdx < 0 || worstPartIdx >= parts.Count) return true;
                var wp = parts[worstPartIdx];
                return wp.nextChoice >= wp.availableMoves.Count;
            }
        }

        /// <summary>
        /// MCTS tree node.
        /// </summary>
        class Node
        {
            public List<Node> children = new List<Node>();
            public Node parent;
            public double visitTimes;
            public double qualityValue = double.MaxValue; // minimum (best) reward seen
            public State state;
        }

        readonly CoACDMesh _initialMesh;
        readonly double _threshold;
        readonly int _mctsNodes;
        readonly int _mctsIterations;
        readonly int _mctsMaxDepth;
        readonly double _rvK;
        readonly Random _rng;

        public MCTSearch(CoACDMesh mesh, double threshold, int mctsNodes, int mctsIterations,
            int mctsMaxDepth, double rvK, Random rng)
        {
            _initialMesh = mesh;
            _threshold = threshold;
            _mctsNodes = mctsNodes;
            _mctsIterations = mctsIterations;
            _mctsMaxDepth = mctsMaxDepth;
            _rvK = rvK;
            _rng = rng;
        }

        /// <summary>
        /// Run MCTS to find the best cutting plane for the given mesh.
        /// Returns the best plane found, and the two resulting halves.
        /// </summary>
        public bool Search(out CoACDPlane bestPlane, out CoACDMesh posMesh, out CoACDMesh negMesh)
        {
            bestPlane = new CoACDPlane();
            posMesh = null;
            negMesh = null;

            var hull = _initialMesh.GetConvexHull();
            double initialRv = ConcavityComputer.ComputeRvCost(_initialMesh, hull, _rvK);
            if (initialRv <= 0) return false;

            // Create root state with just the initial mesh
            var rootPart = new Part
            {
                mesh = _initialMesh,
                hull = hull,
                rv = initialRv,
                availableMoves = GenerateCandidatePlanes(_initialMesh),
                nextChoice = 0
            };

            var rootState = new State
            {
                parts = new List<Part> { rootPart },
                worstPartIdx = 0,
                currentCost = initialRv,
                currentRound = 0,
                terminalThreshold = _threshold,
                maxDepth = _mctsMaxDepth,
                mctsNodes = _mctsNodes
            };

            var root = new Node { state = rootState };

            double explorationC = initialRv / (Math.Sqrt(2) * _mctsMaxDepth);

            // MCTS main loop
            for (int iter = 0; iter < _mctsIterations; iter++)
            {
                if (CoACDEngine.Cancelled)
                    throw new OperationCanceledException("CoACD decomposition cancelled by user.");

                // Selection + Expansion
                Node expandNode = TreePolicy(root, explorationC);

                // Simulation
                double reward = DefaultPolicy(expandNode);

                // Backpropagation
                Backup(expandNode, reward);
            }

            // Select best child (exploitation only)
            if (root.children.Count == 0)
                return false;

            Node bestChild = BestChild(root, 0);
            if (bestChild == null) return false;

            bestPlane = bestChild.state.actionTaken;

            // Ternary refinement
            bestPlane = TernaryRefine(bestPlane, _initialMesh);

            // Perform the final clip
            return MeshClipper.Clip(_initialMesh, bestPlane, out posMesh, out negMesh);
        }

        Node TreePolicy(Node node, double c)
        {
            int safety = 1000;
            while (!node.state.IsTerminal() && safety-- > 0)
            {
                if (node.state.IsAllExpanded())
                {
                    var best = BestChild(node, c);
                    if (best == null) return node;
                    node = best;
                }
                else
                {
                    return Expand(node);
                }
            }
            return node;
        }

        Node Expand(Node node)
        {
            var state = node.state;
            var worstPart = state.parts[state.worstPartIdx];
            int moveIdx = worstPart.nextChoice;
            worstPart.nextChoice++;

            if (moveIdx >= worstPart.availableMoves.Count)
                return node;

            CoACDPlane plane = worstPart.availableMoves[moveIdx];

            // Try to clip the worst part
            CoACDMesh posMesh, negMesh;
            if (!MeshClipper.Clip(worstPart.mesh, plane, out posMesh, out negMesh))
            {
                // Failed clip — create a dead-end child with infinite cost
                var deadState = CloneState(state);
                deadState.currentCost = double.MaxValue;
                deadState.currentRound++;
                deadState.actionTaken = plane;
                deadState.actionPartIdx = state.worstPartIdx;
                var deadChild = new Node { state = deadState, parent = node };
                node.children.Add(deadChild);
                return deadChild;
            }

            // Build new parts list
            var newParts = new List<Part>(state.parts.Count + 1);
            for (int i = 0; i < state.parts.Count; i++)
            {
                if (i == state.worstPartIdx) continue;
                newParts.Add(state.parts[i]);
            }

            var posHull = posMesh.GetConvexHull();
            var negHull = negMesh.GetConvexHull();
            double posRv = ConcavityComputer.ComputeRvCost(posMesh, posHull, _rvK);
            double negRv = ConcavityComputer.ComputeRvCost(negMesh, negHull, _rvK);

            newParts.Add(new Part
            {
                mesh = posMesh,
                hull = posHull,
                rv = posRv,
                availableMoves = GenerateCandidatePlanes(posMesh),
                nextChoice = 0
            });
            newParts.Add(new Part
            {
                mesh = negMesh,
                hull = negHull,
                rv = negRv,
                availableMoves = GenerateCandidatePlanes(negMesh),
                nextChoice = 0
            });

            // Find new worst part
            int worstIdx = 0;
            double worstRv = newParts[0].rv;
            for (int i = 1; i < newParts.Count; i++)
            {
                if (newParts[i].rv > worstRv)
                {
                    worstRv = newParts[i].rv;
                    worstIdx = i;
                }
            }

            var newState = new State
            {
                parts = newParts,
                worstPartIdx = worstIdx,
                currentCost = worstRv,
                currentRound = state.currentRound + 1,
                actionTaken = plane,
                actionPartIdx = state.worstPartIdx,
                terminalThreshold = _threshold,
                maxDepth = _mctsMaxDepth,
                mctsNodes = _mctsNodes
            };

            var child = new Node { state = newState, parent = node };
            node.children.Add(child);
            return child;
        }

        double DefaultPolicy(Node node)
        {
            // Greedy rollout from this state to max depth
            var state = node.state;
            if (state.currentCost >= double.MaxValue * 0.5)
                return double.MaxValue;

            double totalReward = state.currentCost;
            int depth = state.currentRound;

            // Clone parts for simulation
            var simParts = new List<Part>(state.parts.Count);
            for (int i = 0; i < state.parts.Count; i++)
                simParts.Add(state.parts[i]); // shallow copy OK for simulation

            int worstIdx = state.worstPartIdx;
            double worstRv = state.currentCost;

            while (depth < _mctsMaxDepth && worstRv > _threshold)
            {
                if (worstIdx < 0 || worstIdx >= simParts.Count) break;

                var worstPart = simParts[worstIdx];
                if (worstPart.availableMoves == null || worstPart.availableMoves.Count == 0) break;

                // Try a few random planes and pick the best
                int numCandidates = Math.Min(5, worstPart.availableMoves.Count);
                CoACDPlane bestPlane = worstPart.availableMoves[0];
                double bestCost = double.MaxValue;

                for (int c = 0; c < numCandidates; c++)
                {
                    int idx = _rng.Next(worstPart.availableMoves.Count);
                    CoACDPlane candidate = worstPart.availableMoves[idx];

                    CoACDMesh pm, nm;
                    if (!MeshClipper.Clip(worstPart.mesh, candidate, out pm, out nm))
                        continue;

                    // Use approximate hull in rollout — faster with minimal accuracy loss
                    var ph = pm.GetApproxConvexHull();
                    var nh = nm.GetApproxConvexHull();
                    double cost = Math.Max(
                        ConcavityComputer.ComputeRvCost(pm, ph, _rvK),
                        ConcavityComputer.ComputeRvCost(nm, nh, _rvK));

                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestPlane = candidate;
                    }
                }

                if (bestCost >= double.MaxValue * 0.5) break;

                // Apply best plane
                CoACDMesh posM, negM;
                if (!MeshClipper.Clip(worstPart.mesh, bestPlane, out posM, out negM))
                    break;

                // Replace worst part with two new parts
                simParts.RemoveAt(worstIdx);

                var posHull = posM.GetApproxConvexHull();
                var negHull = negM.GetApproxConvexHull();
                simParts.Add(new Part
                {
                    mesh = posM, hull = posHull,
                    rv = ConcavityComputer.ComputeRvCost(posM, posHull, _rvK),
                    availableMoves = GenerateCandidatePlanes(posM),
                    nextChoice = 0
                });
                simParts.Add(new Part
                {
                    mesh = negM, hull = negHull,
                    rv = ConcavityComputer.ComputeRvCost(negM, negHull, _rvK),
                    availableMoves = GenerateCandidatePlanes(negM),
                    nextChoice = 0
                });

                // Find new worst
                worstIdx = 0;
                worstRv = simParts[0].rv;
                for (int i = 1; i < simParts.Count; i++)
                {
                    if (simParts[i].rv > worstRv)
                    {
                        worstRv = simParts[i].rv;
                        worstIdx = i;
                    }
                }

                totalReward += worstRv;
                depth++;
            }

            return totalReward / _mctsMaxDepth;
        }

        Node BestChild(Node node, double c)
        {
            if (node.children.Count == 0) return null;

            Node best = null;
            double bestScore = double.MaxValue;
            double logParent = Math.Log(node.visitTimes + 1);

            for (int i = 0; i < node.children.Count; i++)
            {
                var child = node.children[i];
                if (child.visitTimes < 1e-10) continue;

                double exploitation = child.qualityValue;
                double exploration = c * Math.Sqrt(2.0 * logParent / child.visitTimes);
                double score = exploitation - exploration; // minimization: lower is better

                if (score < bestScore)
                {
                    bestScore = score;
                    best = child;
                }
            }

            // If all children unvisited, pick first
            if (best == null && node.children.Count > 0)
                best = node.children[0];

            return best;
        }

        void Backup(Node node, double reward)
        {
            while (node != null)
            {
                node.visitTimes += 1;
                if (reward < node.qualityValue)
                    node.qualityValue = reward;
                node = node.parent;
            }
        }

        /// <summary>
        /// Ternary search to refine the cutting plane position along its axis.
        /// </summary>
        CoACDPlane TernaryRefine(CoACDPlane plane, CoACDMesh mesh)
        {
            int axis = plane.GetAxis();
            if (axis < 0) return plane;

            double offset = plane.GetOffset();
            double minVal = mesh.bbox[axis * 2];
            double maxVal = mesh.bbox[axis * 2 + 1];
            double range = maxVal - minVal;
            double step = range / (_mctsNodes + 1);

            double left = Math.Max(minVal + step * 0.5, offset - step);
            double right = Math.Min(maxVal - step * 0.5, offset + step);

            double bestCost = EvaluatePlane(CoACDPlane.AxisAligned(axis, offset), mesh);
            CoACDPlane bestPlane = plane;

            for (int iter = 0; iter < 10 && (right - left) > 1e-6; iter++)
            {
                double m1 = left + (right - left) / 3.0;
                double m2 = right - (right - left) / 3.0;

                double c1 = EvaluatePlane(CoACDPlane.AxisAligned(axis, m1), mesh);
                double c2 = EvaluatePlane(CoACDPlane.AxisAligned(axis, m2), mesh);

                if (c1 < c2)
                    right = m2;
                else
                    left = m1;

                double better = Math.Min(c1, c2);
                if (better < bestCost)
                {
                    bestCost = better;
                    bestPlane = CoACDPlane.AxisAligned(axis, c1 < c2 ? m1 : m2);
                }
            }

            return bestPlane;
        }

        double EvaluatePlane(CoACDPlane plane, CoACDMesh mesh)
        {
            CoACDMesh posM, negM;
            if (!MeshClipper.Clip(mesh, plane, out posM, out negM))
                return double.MaxValue;

            var posH = posM.GetConvexHull();
            var negH = negM.GetConvexHull();
            return Math.Max(
                ConcavityComputer.ComputeRvCost(posM, posH, _rvK),
                ConcavityComputer.ComputeRvCost(negM, negH, _rvK));
        }

        /// <summary>
        /// Generate axis-aligned candidate cutting planes for a mesh.
        /// </summary>
        List<CoACDPlane> GenerateCandidatePlanes(CoACDMesh mesh)
        {
            var planes = new List<CoACDPlane>();
            if (mesh.bbox == null) return planes;

            for (int axis = 0; axis < 3; axis++)
            {
                double lo = mesh.bbox[axis * 2];
                double hi = mesh.bbox[axis * 2 + 1];
                double range = hi - lo;
                if (range < 0.01) continue;

                double margin = Math.Max(0.015, range * 0.05);
                double innerLo = lo + margin;
                double innerHi = hi - margin;
                if (innerLo >= innerHi) continue;

                double step = (innerHi - innerLo) / _mctsNodes;
                if (step < 0.01) step = 0.01;

                for (double pos = innerLo; pos <= innerHi + 1e-10; pos += step)
                    planes.Add(CoACDPlane.AxisAligned(axis, pos));
            }

            // Shuffle
            for (int i = planes.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                var tmp = planes[i];
                planes[i] = planes[j];
                planes[j] = tmp;
            }

            return planes;
        }

        State CloneState(State s)
        {
            return new State
            {
                parts = new List<Part>(s.parts),
                worstPartIdx = s.worstPartIdx,
                currentCost = s.currentCost,
                currentRound = s.currentRound,
                terminalThreshold = s.terminalThreshold,
                maxDepth = s.maxDepth,
                mctsNodes = s.mctsNodes
            };
        }
    }
}
