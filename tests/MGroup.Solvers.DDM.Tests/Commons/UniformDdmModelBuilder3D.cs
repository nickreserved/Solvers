namespace MGroup.Solvers.DDM.Tests.Commons
{
	using MGroup.Constitutive.Structural;
	using MGroup.Constitutive.Structural.Continuum;
	using MGroup.Constitutive.Structural.Transient;
	using MGroup.Environments;
	using MGroup.FEM.Structural.Continuum;
	using MGroup.MSolve.Discretization;
	using MGroup.MSolve.Discretization.BoundaryConditions;
	using MGroup.MSolve.Discretization.Dofs;
	using MGroup.MSolve.Discretization.Entities;
	using MGroup.MSolve.Discretization.Meshes.Structured;
	using MGroup.MSolve.Geometry.Coordinates;
	using MGroup.Solvers.DDM.DiscretizationExtensions;
	using MGroup.Solvers.DDM.FetiDP.Dofs;
	using MGroup.Solvers.DDM.Partitioning;
	using MGroup.Solvers.DDM.Tests.DiscretizationExtensions;

	public class UniformDdmModelBuilder3D
	{
		public enum BoundaryRegion
		{
			MinX, MinY, MinZ, MaxX, MaxY, MaxZ,
			MinXMinYMinZ, MinXMinYMaxZ, MinXMaxYMinZ, MinXMaxYMaxZ, MaxXMinYMinZ, MaxXMinYMaxZ, MaxXMaxYMinZ, MaxXMaxYMaxZ,
			Centroid
			//TODO: also the lines MinXMinY, MaxXMinZ, etc
		}

		private List<(BoundaryRegion region, IStructuralDofType dof, double displacement)> prescribedDisplacements;
		private List<(BoundaryRegion region, IStructuralDofType dof, double load)> prescribedLoads;

		public UniformDdmModelBuilder3D()
		{
			prescribedDisplacements = new List<(BoundaryRegion region, IStructuralDofType dof, double displacement)>();
			prescribedLoads = new List<(BoundaryRegion region, IStructuralDofType dof, double load)>();
		}

		public double[] MinCoords { get; set; } = { -1.0, -1.0, -1.0 };

		public double[] MaxCoords { get; set; } = { 1.0, 1.0, +1.0 };

		public int[] NumElementsTotal { get; set; } = { 1, 1, 1 };

		/// <summary>
		/// For each axis d=0,1,2 <see cref="NumElementsPerSubdomainPerAxis"/> contains an int[] array. This array contains
		/// the number of elements of each subdomain. If this property is not provided: a) If the total number of elements along 
		/// an axis is a multiple of the number of subdomains per axis, then that multiplicity will be the number of elements 
		/// per subdomain along that axis. b) If it is not a multiple, then any extra elements will be distributed among a 
		/// subset of the subdomains, starting from the first subdomain of that axis.
		/// </summary>
		public int[][] NumElementsPerSubdomainPerAxis { get; set; } = null;

		public int[] NumSubdomains { get; set; } = { 1, 1, 1 };

		public int[] NumClusters { get; set; } = { 1, 1, 1 };

		public IContinuumMaterial3D MaterialHomogeneous { get; set; } = new ElasticMaterial3D(1.0, 0.3);

		public Func<int[], IContinuumMaterial3D> GetMaterialPerElementIndex { get; set; }

		public (Model model, ComputeNodeTopology nodeTopology) BuildMultiSubdomainModel()
		{
			Model model = BuildSingleSubdomainModel();
			UniformCartesianMesh3D mesh = BuildMesh();
			var partitioner = new UniformMeshPartitioner3D(mesh, NumSubdomains, NumClusters, NumElementsPerSubdomainPerAxis);
			partitioner.Partition(model);
			ModelUtilities.DecomposeIntoSubdomains(model, partitioner.NumSubdomainsTotal, partitioner.GetSubdomainOfElement);

			var topology = new ComputeNodeTopology();
			for (int s = 0; s < partitioner.NumSubdomainsTotal; ++s)
			{
				topology.AddNode(s, partitioner.GetNeighboringSubdomains(s), partitioner.GetClusterOfSubdomain(s));
			}

			return (model, topology);
		}

		public Model BuildSingleSubdomainModel()
		{
			var model = new Model();
			model.SubdomainsDictionary[0] = new Subdomain(0);

			UniformCartesianMesh3D mesh = BuildMesh();

			// Nodes
			foreach ((int id, double[] coords) in mesh.EnumerateNodes())
			{
				model.NodesDictionary[id] = new Node(id, coords[0], coords[1], coords[2]);
			}

			// Elements
			var dynamicProperties = new TransientAnalysisProperties(1.0, 1.0, 1.0);
			var elemFactory = new ContinuumElement3DFactory(MaterialHomogeneous, dynamicProperties);
			foreach ((int elementID, int[] nodeIDs) in mesh.EnumerateElements())
			{
				// Identify which material to use
				IContinuumMaterial3D elementMaterial;
				if (GetMaterialPerElementIndex == null)
				{
					elementMaterial = MaterialHomogeneous;
				}
				else
				{
					int[] elementIdx = mesh.GetElementIdx(elementID);
					elementMaterial = GetMaterialPerElementIndex(elementIdx);
				}

				INode[] nodes = nodeIDs.Select(n => model.NodesDictionary[n]).ToArray();
				var element = elemFactory.CreateElement(mesh.CellType, nodes, elementMaterial, dynamicProperties);
				element.ID = elementID;
				model.ElementsDictionary[element.ID] = element;
				model.SubdomainsDictionary[0].Elements.Add(element);
			}

			ApplyBoundaryConditions(model);

			return model;
		}

		/// <summary>
		/// If there are nodes belonging to <paramref name="region"/> taht are constrained along <paramref name="dof"/>, then 
		/// they will not be loaded, but the total <paramref name="load"/> will be divided by the total count of nodes, even the
		/// ones that will not be loaded.
		/// If there are multiple loads at the same (node, dof) then their sum will be used.
		/// </summary>
		/// <param name="load">Will be distributed evenly.</param>
		public void DistributeLoadAtNodes(BoundaryRegion region, IStructuralDofType dof, double load)
			=> prescribedLoads.Add((region, dof, load));

		public void PrescribeDisplacement(BoundaryRegion region, IStructuralDofType dof, double displacement)
			=> prescribedDisplacements.Add((region, dof, displacement));

		public static ICornerDofSelection FindCornerDofs(IModel model, int minCornerNodeMultiplicity = 3)
		{
			var cornerNodes = new HashSet<int>();
			foreach (ISubdomain subdomain in model.EnumerateSubdomains())
			{
				INode[] subdomainCorners = CornerNodeUtilities.FindCornersOfBrick3D(subdomain);
				foreach (INode node in subdomainCorners)
				{
					INodalDirichletBoundaryCondition<IDofType>[] constraints = model.FindDirichletBCsOfNode(node, subdomain.ID);
					if (constraints.Length > 0) //TODO: allow only some dofs to be constrained
					{
						continue;
					}

					if (node.Subdomains.Count >= minCornerNodeMultiplicity)
					{
						cornerNodes.Add(node.ID);
					}
				}
			}

			var cornerDofs = new UserDefinedCornerDofSelection();
			foreach (int node in cornerNodes)
			{
				cornerDofs.AddCornerNode(node);
			}
			return cornerDofs;
		}

		private void ApplyBoundaryConditions(Model model)
		{
			var bcBuilder = new IncrementalBCBuilderStructural(model);
			double dx = (MaxCoords[0] - MinCoords[0]) / NumElementsTotal[0];
			double dy = (MaxCoords[1] - MinCoords[1]) / NumElementsTotal[1];
			double dz = (MaxCoords[2] - MinCoords[2]) / NumElementsTotal[1];
			double meshTolerance = 1E-10 * Math.Min(dx, Math.Min(dy, dz));

			// Apply prescribed Dirichlet BCs
			foreach ((BoundaryRegion region, IStructuralDofType dof, double displacement) in prescribedDisplacements)
			{
				INode[] nodes = FindBoundaryNodes(region, model, meshTolerance);
				foreach (INode node in nodes)
				{
					bcBuilder.AddDirichletBC(node, dof, displacement);
				}
			}

			// Apply prescribed loads
			foreach ((BoundaryRegion region, IStructuralDofType dof, double totalLoad) in prescribedLoads)
			{
				INode[] nodes = FindBoundaryNodes(region, model, meshTolerance);
				double load = totalLoad / nodes.Length;
				foreach (INode node in nodes)
				{
					bcBuilder.AddNeumannBC(node, dof, load);
				}
			}

			bcBuilder.ConfirmBoundaryConditions();
		}

		private UniformCartesianMesh3D BuildMesh()
		{
			return new UniformCartesianMesh3D.Builder(MinCoords, MaxCoords, NumElementsTotal)
				.SetMajorMinorAxis(0, 2).SetElementNodeOrderBathe().BuildMesh();
		}

		private INode[] FindBoundaryNodes(BoundaryRegion region, Model model, double tol)
		{
			double minX = MinCoords[0], minY = MinCoords[1], minZ = MinCoords[2];
			double maxX = MaxCoords[0], maxY = MaxCoords[1], maxZ = MaxCoords[2];
			IEnumerable<INode> nodes;
			if (region == BoundaryRegion.MinX)
			{
				nodes = model.NodesDictionary.Values.Where(node => Math.Abs(node.X - minX) <= tol);
			}
			else if (region == BoundaryRegion.MinY)
			{
				nodes = model.NodesDictionary.Values.Where(node => Math.Abs(node.Y - minY) <= tol);
			}
			else if (region == BoundaryRegion.MinZ)
			{
				nodes = model.NodesDictionary.Values.Where(node => Math.Abs(node.Z - minZ) <= tol);
			}
			else if (region == BoundaryRegion.MaxX)
			{
				nodes = model.NodesDictionary.Values.Where(node => Math.Abs(node.X - maxX) <= tol);
			}
			else if (region == BoundaryRegion.MaxY)
			{
				nodes = model.NodesDictionary.Values.Where(node => Math.Abs(node.Y - maxY) <= tol);
			}
			else if (region == BoundaryRegion.MaxZ)
			{
				nodes = model.NodesDictionary.Values.Where(node => Math.Abs(node.Z - maxZ) <= tol);
			}
			else if (region == BoundaryRegion.MinXMinYMinZ)
			{
				nodes = model.NodesDictionary.Values.Where(node =>
					(Math.Abs(node.X - minX) <= tol) && (Math.Abs(node.Y - minY) <= tol) && (Math.Abs(node.Z - minZ) <= tol));
			}
			else if (region == BoundaryRegion.MinXMinYMaxZ)
			{
				nodes = model.NodesDictionary.Values.Where(node =>
					(Math.Abs(node.X - minX) <= tol) && (Math.Abs(node.Y - minY) <= tol) && (Math.Abs(node.Z - maxZ) <= tol));
			}
			else if (region == BoundaryRegion.MinXMaxYMinZ)
			{
				nodes = model.NodesDictionary.Values.Where(node =>
					(Math.Abs(node.X - minX) <= tol) && (Math.Abs(node.Y - maxY) <= tol) && (Math.Abs(node.Z - minZ) <= tol));
			}
			else if (region == BoundaryRegion.MinXMaxYMaxZ)
			{
				nodes = model.NodesDictionary.Values.Where(node =>
					(Math.Abs(node.X - minX) <= tol) && (Math.Abs(node.Y - maxY) <= tol) && (Math.Abs(node.Z - maxZ) <= tol));
			}
			else if (region == BoundaryRegion.MaxXMinYMinZ)
			{
				nodes = model.NodesDictionary.Values.Where(node =>
					(Math.Abs(node.X - maxX) <= tol) && (Math.Abs(node.Y - minY) <= tol) && (Math.Abs(node.Z - minZ) <= tol));
			}
			else if (region == BoundaryRegion.MaxXMinYMaxZ)
			{
				nodes = model.NodesDictionary.Values.Where(node =>
					(Math.Abs(node.X - maxX) <= tol) && (Math.Abs(node.Y - minY) <= tol) && (Math.Abs(node.Z - maxZ) <= tol));
			}
			else if (region == BoundaryRegion.MaxXMaxYMinZ)
			{
				nodes = model.NodesDictionary.Values.Where(node =>
					(Math.Abs(node.X - maxX) <= tol) && (Math.Abs(node.Y - maxY) <= tol) && (Math.Abs(node.Z - minZ) <= tol));
			}
			else if (region == BoundaryRegion.MaxXMaxYMaxZ)
			{
				nodes = model.NodesDictionary.Values.Where(node =>
					(Math.Abs(node.X - maxX) <= tol) && (Math.Abs(node.Y - maxY) <= tol) && (Math.Abs(node.Z - maxZ) <= tol));
			}
			else if (region == BoundaryRegion.Centroid)
			{
				if ((NumElementsTotal[0] % 2 != 0) || (NumElementsTotal[1] % 2 != 0) || (NumElementsTotal[2] % 2 != 0))
				{
					throw new ArgumentException(
						"To manipulate the node at the centre, the number of elements in each axis must be even");
				}

				double centerX = 0.5 * (minX + maxX);
				double centerY = 0.5 * (minY + maxY);
				double centerZ = 0.5 * (minZ + maxZ);
				var centroid = new CartesianPoint(centerX, centerY, centerZ);

				// LINQ note: if you call Min() on a sequence of tuples, then the tuple that has minimum Item1 will be returned
				INode centroidNode = model.NodesDictionary.Values.Select(n => (n.CalculateDistanceFrom(centroid), n)).Min().Item2;
				nodes = new INode[] { centroidNode };
			}
			else throw new Exception("Should not have reached this code");

			return nodes.ToArray();
		}
	}
}
