namespace MGroup.Solvers.DDM.FetiDP.Dofs
{
	using MGroup.Solvers.DDM.LinearSystem;

	public class NullModifiedCornerDofs : IModifiedCornerDofs
	{
		public bool AreGlobalCornerDofsModified => true;

		public bool AreSubdomainCornerDofsModified(int subdomainID) => true;

		public void Update(IModifiedSubdomains modifiedSubdomains)
		{
		}
	}
}
