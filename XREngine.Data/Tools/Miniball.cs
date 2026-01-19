using Extensions;
using System.Numerics;
using FloatingPoint = System.Single;

namespace XREngine.Maths
{
    public class Miniball
    {
        private readonly static FloatingPoint Eps = (FloatingPoint)1e-14;

        private readonly IPointSet S;
        private readonly int size;
        private readonly int dim;
        private int iteration;
        private readonly FloatingPoint[] center, centerToAff, centerToPoint, lambdas;
        private FloatingPoint distToAff, distToAffSquare;
        private FloatingPoint squaredRadius, radius;
        private readonly Subspan? support;
        private int stopper;

        public Miniball(IPointSet pts)
        {
            S = pts;
            size = S.Size;
            dim = S.Dimensions;
            center = new FloatingPoint[dim];
            centerToAff = new FloatingPoint[dim];
            centerToPoint = new FloatingPoint[dim];
            lambdas = new FloatingPoint[dim + 1];
            support = InitBall();
            Compute();
        }
        private Subspan Support
            => support ?? throw new InvalidOperationException("Miniball is empty.");
        public bool IsEmpty => size == 0;
        public FloatingPoint Radius => radius;
        public FloatingPoint SquaredRadius => squaredRadius;
        public FloatingPoint[] Center => center;
        public int Size => size;

        private static FloatingPoint Sqr(FloatingPoint x) => x * x;
        
        private Subspan? InitBall()
        {
            //assert size > 0;

            if (size == 0)
                return null;

            // Set center to the first point in S
            for (int i = 0; i < dim; ++i)
                center[i] = S.Coord(0, i);

            // Find farthest point
            squaredRadius = 0;
            int farthest = 0;
            for (int j = 1; j < S.Size; ++j)
            {
                // Compute squared distance from center to S[j]
                FloatingPoint dist = 0;
                for (int i = 0; i < dim; ++i)
                    dist += Sqr(S.Coord(j, i) - center[i]);

                // enlarge radius if needed:
                if (dist >= squaredRadius)
                {
                    squaredRadius = dist;
                    farthest = j;
                }
            }
            radius = (FloatingPoint)Math.Sqrt(squaredRadius);

            // Initialize support to the farthest point:
            return new Subspan(dim, S, farthest);
        }
        private void ComputeDistToAff()
        {
            distToAffSquare = Support.ShortestVectorToSpan(center, centerToAff);
            distToAff = (FloatingPoint)Math.Sqrt(distToAffSquare);
        }
        private void UpdateRadius()
        {
            int any = Support.AnyMember();
            squaredRadius = 0;
            for (int i = 0; i < dim; ++i)
                squaredRadius += Sqr(S.Coord(any, i) - center[i]);
            radius = (FloatingPoint)Math.Sqrt(squaredRadius);
            //if (log) debug("current radius = " + radius);
        }
        private void Compute()
        {
            if (size == 0)
                return;

            // Invariant: The ball B(center,radius) always contains the whole
            // point set S and has the points in support on its boundary.
            while (true)
            {
                ++iteration;

                //if (log)
                //{
                //    debug("Iteration " + iteration);
                //    debug(support.size() + " points on the boundary");
                //}

                // Compute a walking direction and walking vector,
                // and check if the former is perhaps too small:
                ComputeDistToAff();
                while (distToAff <= Eps * radius ||
                /*
                 * Note: the following line is currently needed because of point sets like schnartz, see
                 * MiniballTest.
                 */
                Support.Size== dim + 1)
                {
                    // We are closer than Eps * radius_square, so we try a drop
                    if (!SuccessfulDrop())
                    {
                        // If that is not possible, the center lies in the convex hull
                        // and we are done.
                        //if (log) info("Done");
                        return;
                    }
                    ComputeDistToAff();
                }
                // if (log) debug("distance to affine hull = " + distToAff);

                // Determine how far we can walk in direction centerToAff
                // without losing any point ('stopper', say) in S:
                FloatingPoint scale = FindStopFraction();

                // Stopping point exists
                if (stopper >= 0)
                {
                    // Walk as far as we can
                    for (int i = 0; i < dim; ++i)
                        center[i] += scale * centerToAff[i];

                    UpdateRadius();

                    // and add stopper to support
                    Support.Add(stopper);
                    // if (log) debug("adding global point #" + stopper);

                    // No obstacle on our way into the affine hull
                }
                else
                {
                    for (int i = 0; i < dim; ++i)
                        center[i] += centerToAff[i];

                    UpdateRadius();

                    // Theoretically, the distance to the affine hull is now zero
                    // and we would thus drop a point in the next iteration.
                    // For numerical stability, we don't rely on that to happen but
                    // try to drop a point right now:
                    if (!SuccessfulDrop())
                    {
                        // Drop failed, so the center lies in conv(support) and is thus optimal.
                        return;
                    }
                }
            }
        }
        bool SuccessfulDrop()
        {
            // Find coefficients of the affine combination of center
            Support.FindAffineCoefficients(center, lambdas);

            // find a non-positive coefficient
            int smallest = 0;
            FloatingPoint minimum = 1;
            for (int i = 0; i < Support.Size; ++i)
                if (lambdas[i] < minimum)
                {
                    minimum = lambdas[i];
                    smallest = i;
                }

            // Drop a point with non-positive coefficient, if any
            if (minimum <= 0)
            {
                // if (log) debug("removing local point #" + smallest);
                Support.Remove(smallest);
                return true;
            }
            return false;
        }
        private FloatingPoint FindStopFraction()
        {
            // We would like to walk the full length of centerToAff ...
            FloatingPoint scale = 1;
            stopper = -1;

            // ... but one of the points in S might hinder us
            for (int j = 0; j < size; ++j)
                if (!Support.IsMember(j))
                {
                    // Compute vector centerToPoint from center to the point S[j]:
                    for (int i = 0; i < dim; ++i)
                        centerToPoint[i] = S.Coord(j, i) - center[i];

                    FloatingPoint dirPointProd = 0;
                    for (int i = 0; i < dim; ++i)
                        dirPointProd += centerToAff[i] * centerToPoint[i];

                    // We can ignore points beyond support since they stay enclosed anyway
                    if (distToAffSquare - dirPointProd < Eps * radius * distToAff) continue;

                    // Compute the fraction we can walk along centerToAff until
                    // we hit point S[i] on the boundary.
                    // (Better don't try to understand this calculus from the code,
                    // it needs some pencil-and-paper work.)
                    FloatingPoint bound = 0;
                    for (int i = 0; i < dim; ++i)
                        bound += centerToPoint[i] * centerToPoint[i];
                    bound = (squaredRadius - bound) / 2 / (distToAffSquare - dirPointProd);

                    // Take the smallest fraction
                    if (bound > 0 && bound < scale)
                    {
                        //if (log) debug("found stopper " + j + " bound=" + bound + " scale=" + scale);
                        scale = bound;
                        stopper = j;
                    }
                }

            return scale;
        }
        public Quality Verify()
        {
            FloatingPoint min_lambda = 1; // for center-in-convex-hull check
            FloatingPoint max_overlength = 0; // for all-points-in-ball check
            FloatingPoint min_underlength = 0; // for all-boundary-points-on-boundary
            FloatingPoint ball_error;
            if (size == 0)
                return new Quality(0, 0, 0, 0, iteration, 0);

            FloatingPoint qr_error = Support.RepresentationError();

            // Center really in convex hull?
            Support.FindAffineCoefficients(center, lambdas);
            for (int k = 0; k < Support.Size; ++k)
                if (lambdas[k] <= min_lambda) min_lambda = lambdas[k];

            // All points in ball, all support points really on boundary?
            for (int k = 0; k < S.Size; ++k)
            {

                // Compare center-to-point distance with radius
                for (int i = 0; i < dim; ++i)
                    centerToPoint[i] = S.Coord(k, i) - center[i];
                FloatingPoint sqDist = 0;
                for (int i = 0; i < dim; ++i)
                    sqDist += Sqr(centerToPoint[i]);
                ball_error = (FloatingPoint)Math.Sqrt(sqDist) - radius;

                // Check for sphere violations
                if (ball_error > max_overlength) max_overlength = ball_error;

                // check for boundary violations
                if (Support.IsMember(k)) if (ball_error < min_underlength) min_underlength = ball_error;
            }

            return new Quality(qr_error, min_lambda, max_overlength / radius, Math.Abs(min_underlength / radius), iteration, Support.Size);
        }
        //public String toString()
        //{
        //    StringBuilder s = new StringBuilder("Miniball [");
        //    if (isEmpty())
        //    {
        //        s.append("isEmpty=true");
        //    }
        //    else
        //    {
        //        s.append("center=(");
        //        for (int i = 0; i < dim; ++i)
        //        {
        //            s.append(center[i]);
        //            if (i < dim - 1) s.append(", ");
        //        }
        //        s.append("), radius=")
        //            .append(radius)
        //            .append(", squaredRadius=")
        //            .append(squaredRadius)
        //            .append(", quality=")
        //            .append(verify());
        //    }
        //    return s.append("]").toString();
        //}
    }
    
    public class PointSetArray(int dimensions, int size) : IPointSet
    {
        private readonly FloatingPoint[] _values = new FloatingPoint[size * dimensions];

        public int Size =>  size;
        public int Dimensions =>  dimensions;

        public static PointSetArray FromVectors(Vector4[] points)
        {
            PointSetArray p = new(4, points.Length);
            for (int i = 0; i < points.Length; ++i)
                for (int j = 0; j < 4; ++j)
                    p._values[i * 4 + j] = points[i][j];
            return p;
        }
        public static PointSetArray FromVectors(Vector3[] points)
        {
            PointSetArray p = new(3, points.Length);
            for (int i = 0; i < points.Length; ++i)
                for (int j = 0; j < 3; ++j)
                    p._values[i * 3 + j] = points[i][j];
            return p;
        }
        public static PointSetArray FromVectors(Vector2[] points)
        {
            PointSetArray p = new(2, points.Length);
            for (int i = 0; i < points.Length; ++i)
                for (int j = 0; j < 2; ++j)
                    p._values[i * 2 + j] = points[i][j];
            return p;
        }
        public static PointSetArray FromVectors(FloatingPoint[] points)
        {
            PointSetArray p = new(1, points.Length);
            for (int i = 0; i < points.Length; ++i)
                p._values[i] = points[i];
            return p;
        }

        public FloatingPoint Coord(int i, int j)
        {
            //assert 0 <= i && i < n;
            //assert 0 <= j && j < d;
            return _values[i * dimensions + j];
        }

        /// <summary>
        /// Sets the j'th Euclidean coordinate of the i'th point to the given value.
        /// </summary>
        /// <param name="i">the number of the point, 0 ≤ i < size</param>
        /// <param name="j">the dimension of the coordinate of interest, 0 ≤ j ≤ dimension</param>
        /// <param name="v">the value to set</param>
        public void SetValue(int i, int j, FloatingPoint v)
        {
            //assert 0 <= i && i < n;
            //assert 0 <= j && j < d;
            _values[i * dimensions + j] = v;
        }

        //public String toString()
        //{
        //    StringBuffer s = new StringBuffer("{");
        //    for (int i = 0; i < n; ++i)
        //    {
        //        s.append('[');
        //        for (int j = 0; j < d; ++j)
        //        {
        //            s.append(coord(i, j));
        //            if (j < d - 1) s.append(",");
        //        }
        //        s.append(']');
        //        if (i < n - 1) s.append(", ");
        //    }
        //    s.append('}');
        //    return s.toString();
        //}
    }

    public interface IPointSet
    {
        int Size { get; }
        int Dimensions { get; }
        FloatingPoint Coord(int i, int j);
    }

    public class BitSet(int size)
    {
        private readonly bool[] _bits = new bool[size];

        public bool Get(int i)
            => _bits.IndexInRangeArrayT(i) && _bits[i];
        public void Set(int i)
        {
            if (!_bits.IndexInRangeArrayT(i))
                _bits.Resize(i + 1);
            _bits[i] = true;
        }
        public void Clear(int i)
        {
            if (_bits.IndexInRangeArrayT(i))
                _bits[i] = false;
        }
    }

    public class Subspan
    {
        private readonly IPointSet _points;
        private readonly BitSet membership;
        private readonly int dim;
        private readonly int[] members;
        private readonly FloatingPoint[][] Q, R;
        private readonly FloatingPoint[] u, w;
        private int r;
        private FloatingPoint c, s;

        public Subspan(int dim, IPointSet points, int k)
        {
            _points = points;
            this.dim = dim;
            membership = new BitSet(points.Size);
            members = new int[dim + 1];
            r = 0;

            // Allocate storage for Q, R, u, and w
            Q = new FloatingPoint[dim][];
            R = new FloatingPoint[dim][];
            for (int i = 0; i < dim; ++i)
            {
                Q[i] = new FloatingPoint[dim];
                R[i] = new FloatingPoint[dim];
            }
            u = new FloatingPoint[dim];
            w = new FloatingPoint[dim];

            // Initialize Q to the identity matrix:
            for (int i = 0; i < dim; ++i)
                for (int j = 0; j < dim; ++j)
                    Q[j][i] = (FloatingPoint)((i == j) ? 1.0 : 0.0);

            members[r] = k;
            membership.Set(k);

            //if (log) info("rank: " + r);
        }
        public int Dimension
        {
            get
            {
                return dim;
            }
        }

        public int Size
        {
            get
            {
                return r + 1;
            }
        }

        public bool IsMember(int i)
        {
            //assert 0 <= i && i < S.size();
            return membership.Get(i);
        }
        public int AnyMember()
        {
            //assert size() > 0;
            return members[r];
        }
        public int GlobalIndex(int i)
        {
            //assert 0 <= i && i < size();
            return members[i];
        }
        //private int Ind(int i, int j)
        //{
        //    return i * dim + j;
        //}
        private int Origin()
        {
            return members[r];
        }
        private void Givens(FloatingPoint a, FloatingPoint b)
        {
            if (b == 0.0)
            {
                c = (FloatingPoint)1.0;
                s = (FloatingPoint)0.0;
            }
            else if (Math.Abs(b) > Math.Abs(a))
            {
                FloatingPoint t = a / b;
                s = 1 / (FloatingPoint)Math.Sqrt(1 + t * t);
                c = s * t;
            }
            else
            {
                FloatingPoint t = b / a;
                c = 1 / (FloatingPoint)Math.Sqrt(1 + t * t);
                s = c * t;
            }
        }
        private void AppendColumn()
        {
            //assert r<dim;

            // Compute new column R[r] = Q^T * u
            for (int i = 0; i < dim; ++i)
            {
                R[r][i] = 0;
                for (int k = 0; k < dim; ++k)
                    R[r][i] += Q[i][k] * u[k];
            }

            // Zero all entries R[r][dim-1] down to R[r][r+1]
            for (int j = dim - 1; j > r; --j)
            {
                // Note: j is the index of the entry to be cleared with the help of entry j-1.

                // Compute Givens coefficients c,s
                Givens(R[r][j - 1], R[r][j]); // PERF: inline

                // Rotate one R-entry (the other one is an implicit zero)
                R[r][j - 1] = c * R[r][j - 1] + s * R[r][j];

                // Rotate two Q-columns
                for (int i = 0; i < dim; ++i)
                {
                    FloatingPoint a = Q[j - 1][i];
                    FloatingPoint b = Q[j][i];
                    Q[j - 1][i] = c * a + s * b;
                    Q[j][i] = c * b - s * a;
                }
            }
        }
        public void Add(int index)
        {
            //assert !isMember(index);

            // Compute S[i] - origin into u
            int o = Origin();
            for (int i = 0; i < dim; ++i)
                u[i] = _points.Coord(index, i) - _points.Coord(o, i);

            // Appends new column u to R and updates QR-decomposition (note: routine works with old r)
            AppendColumn();

            // move origin index and insert new index:
            membership.Set(index);
            members[r + 1] = members[r];
            members[r] = index;
            ++r;

            //info("rank: " + r);
        }
        public FloatingPoint ShortestVectorToSpan(FloatingPoint[] p, FloatingPoint[] w)
        {
            // Compute vector from p to origin, i.e., w = origin - p
            int o = Origin();
            for (int i = 0; i < dim; ++i)
                w[i] = _points.Coord(o, i) - p[i];

            // Remove projections of w onto the affine hull
            for (int j = 0; j < r; ++j)
            {
                FloatingPoint scale = 0;
                for (int i = 0; i < dim; ++i)
                    scale += w[i] * Q[j][i];
                for (int i = 0; i < dim; ++i)
                    w[i] -= scale * Q[j][i];
            }

            FloatingPoint sl = 0;
            for (int i = 0; i < dim; ++i)
                sl += w[i] * w[i];
            return sl;
        }
        public FloatingPoint RepresentationError()
        {
            FloatingPoint[] lambdas = new FloatingPoint[Size];
            FloatingPoint[] pt = new FloatingPoint[dim];
            FloatingPoint max = 0, error;

            // Cycle through all points in hull
            for (int j = 0; j < Size; ++j)
            {
                // Get point
                for (int i = 0; i < dim; ++i)
                    pt[i] = _points.Coord(GlobalIndex(j), i);

                // Compute the affine representation:
                FindAffineCoefficients(pt, lambdas);

                // compare coefficient of point j to 1.0
                error = (FloatingPoint)Math.Abs(lambdas[j] - 1.0);
                if (error > max) max = error;

                // compare the other coefficients against 0.0
                for (int i = 0; i < j; ++i)
                {
                    error = (FloatingPoint)Math.Abs(lambdas[i] - 0.0);
                    if (error > max) max = error;
                }
                for (int i = j + 1; i < Size; ++i)
                {
                    error = (FloatingPoint)Math.Abs(lambdas[i] - 0.0);
                    if (error > max) max = error;
                }
            }

            return max;
        }
        public void FindAffineCoefficients(FloatingPoint[] p, FloatingPoint[] lambdas)
        {
            // Compute relative position of p, i.e., u = p - origin
            int o = Origin();
            for (int i = 0; i < dim; ++i)
                u[i] = p[i] - _points.Coord(o, i);

            // Calculate Q^T u into w
            for (int i = 0; i < dim; ++i)
            {
                w[i] = 0;
                for (int k = 0; k < dim; ++k)
                    w[i] += Q[i][k] * u[k];
            }

            // We compute the coefficients by backsubstitution. Notice that
            //
            // c = \sum_{i\in M} \lambda_i (S[i] - origin)
            // = \sum_{i\in M} \lambda_i S[i] + (1-s) origin
            //
            // where s = \sum_{i\in M} \lambda_i.-- We compute the coefficient
            // (1-s) of the origin in the variable origin_lambda:
            FloatingPoint origin_lambda = 1;
            for (int j = r - 1; j >= 0; --j)
            {
                for (int k = j + 1; k < r; ++k)
                    w[j] -= lambdas[k] * R[k][j];
                FloatingPoint lj = w[j] / R[j][j];
                lambdas[j] = lj;
                origin_lambda -= lj;
            }
            // The r-th coefficient corresponds to the origin (see remove()):
            lambdas[r] = origin_lambda;
        }
        private void Hessenberg_clear(int pos)
        {
            // Clear new subdiagonal entries
            for (; pos < r; ++pos)
            {
                // Note: pos is the column index of the entry to be cleared

                // Compute Givens coefficients c,s
                Givens(R[pos][pos], R[pos][pos + 1]); // PERF: inline

                // Rotate partial R-rows (of the first pair, only one entry is
                // needed, the other one is an implicit zero)
                R[pos][pos] = c * R[pos][pos] + s * R[pos][pos + 1];
                // Then begin at position pos+1
                for (int j = pos + 1; j < r; ++j)
                {
                    FloatingPoint a = R[j][pos];
                    FloatingPoint b = R[j][pos + 1];
                    R[j][pos] = c * a + s * b;
                    R[j][pos + 1] = c * b - s * a;
                }

                // Rotate Q-columns
                for (int i = 0; i < dim; ++i)
                {
                    FloatingPoint a = Q[pos][i];
                    FloatingPoint b = Q[pos + 1][i];
                    Q[pos][i] = c * a + s * b;
                    Q[pos + 1][i] = c * b - s * a;
                }
            }
        }
        private void Special_rank_1_update()
        {
            // Compute w = Q^T * u
            for (int i = 0; i < dim; ++i)
            {
                w[i] = 0;
                for (int k = 0; k < dim; ++k)
                    w[i] += Q[i][k] * u[k];
            }

            // Rotate w down to a multiple of the first unit vector;
            // the operations have to be recorded in R and Q
            for (int k = dim - 1; k > 0; --k)
            {
                // Note: k is the index of the entry to be cleared with the help of entry k-1.

                // Compute Givens coefficients c,s
                Givens(w[k - 1], w[k]);

                // rotate w-entry
                w[k - 1] = c * w[k - 1] + s * w[k];

                // Rotate two R-rows;
                // the first column has to be treated separately
                // in order to account for the implicit zero in R[k-1][k]
                R[k - 1][k] = -s * R[k - 1][k - 1];
                R[k - 1][k - 1] *= c;
                for (int j = k; j < r; ++j)
                {
                    FloatingPoint a = R[j][k - 1];
                    FloatingPoint b = R[j][k];
                    R[j][k - 1] = c * a + s * b;
                    R[j][k] = c * b - s * a;
                }

                // Rotate two Q-columns
                for (int i = 0; i < dim; ++i)
                {
                    FloatingPoint a = Q[k - 1][i];
                    FloatingPoint b = Q[k][i];
                    Q[k - 1][i] = c * a + s * b;
                    Q[k][i] = c * b - s * a;
                }
            }

            // Add w * (1,...,1)^T to new R, which means simply to add u[0] to each column
            // since the other entries of u have just been eliminated
            for (int j = 0; j < r; ++j)
                R[j][0] += w[0];

            // Clear subdiagonal entries
            Hessenberg_clear(0);
        }
        public void Remove(int index)
        {
            //assert isMember(globalIndex(index)) && size() > 1;

            membership.Clear(GlobalIndex(index));

            if (index == r)
            {
                // Origin must be deleted.
                int o = Origin();

                // We choose the right-most member of Q, i.e., column r-1 of R,
                // as the new origin. So all relative vectors (i.e., the
                // columns of "A = QR") have to be updated by u:= old origin -
                // S[global_index(r-1)]:
                int gi = GlobalIndex(r - 1);
                for (int i = 0; i < dim; ++i)
                    u[i] = _points.Coord(o, i) - _points.Coord(gi, i);

                --r;

                //if (log) info("rank: " + r);

                Special_rank_1_update();

            }
            else
            {
                // General case: delete column from R

                // Shift higher columns of R one step to the left
                FloatingPoint[] dummy = R[index];
                for (int j = index + 1; j < r; ++j)
                {
                    R[j - 1] = R[j];
                    members[j - 1] = members[j];
                }
                members[r - 1] = members[r]; // Shift down origin
                R[--r] = dummy; // Relink trash column

                // Zero out subdiagonal entries in R
                Hessenberg_clear(index);
            }
        }
    }
    /// <summary>
    /// Information about the quality of the computed ball.
    /// </summary>
    public class Quality(
        FloatingPoint qrInconsistency,
        FloatingPoint minConvexCoefficient,
        FloatingPoint maxOverlength,
        FloatingPoint maxUnderlength,
        int iterations,
        int supportSize)
    {
        /// <summary>
        /// A measure for the quality of the internally used support points.
        /// The returned number should in theory be zero (but may be non-zero due to rounding errors).
        /// </summary>
        public FloatingPoint QrInconsistency => qrInconsistency;
        /// <summary>
        /// A measure for the minimality of the computed ball.
        /// The returned number should in theory be non-zero and positive.Due to rounding errors, it may be negative.
        /// </summary>
        public FloatingPoint MinConvexCoefficient => minConvexCoefficient;
        /// <summary>
        /// The maximal over-length of a point from the input set, relative to the computed miniball's
        /// radius.
        /// For each point <i>p</i> from the input point set, it is computed how far it is <i>outside</i>
        /// the miniball ("over-length"). The returned number is the maximal such over-length, divided by
        /// the radius of the computed miniball.
        /// Notice that getMaxOverlength() == 0 if and only if all points are contained in the
        /// miniball.
        /// @return the maximal over-length, a number ≥ 0
        /// </summary>
        public FloatingPoint MaxOverlength => maxOverlength;
        /// <summary>
        /// The maximal under-length of a point from the input set, relative to the computed miniball's radius.
        /// For each point <i>p</i> from the input point set, it is computed how far one has to walk from
        /// this point towards the boundary of the miniball ("under-length"). The returned number is the
        /// maximal such under-length, divided by the radius of the computed miniball.
        /// Notice that in theory MaxUnderlength should be zero, otherwise the computed
        /// miniball is enclosing but not minimal.
        /// Returns the maximal under-length, a number ≥ 0.
        /// </summary>
        public FloatingPoint MaxUnderlength => maxUnderlength;
        /// <summary>
        /// The number of iterations that the algorithm needed to compute the miniball.
        /// </summary>
        public int Iterations => iterations;
        /// <summary>
        /// The size of the support.
        /// </summary>
        public int SupportSize => supportSize;

        public override string ToString()
        {
            return "Quality [qrInconsistency="
                + qrInconsistency
                + ", minConvexCoefficient="
                + minConvexCoefficient
                + ", maxOverlength="
                + maxOverlength
                + ", maxUnderlength="
                + maxUnderlength
                + ", iterations="
                + iterations
                + ", supportSize="
                + supportSize
                + "]";
        }
    }
}