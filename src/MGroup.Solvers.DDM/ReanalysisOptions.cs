namespace MGroup.Solvers.DDM
{
	using MGroup.Solvers.DDM.LinearSystem;

	public class ReanalysisOptions
	{
		protected ReanalysisOptions(bool commonValueForFlags, IModifiedSubdomains modifiedSubdomains)
		{
			this.ModifiedSubdomains = modifiedSubdomains;
			IntersubdomainFreeDofs = commonValueForFlags;
			SubdomainFreeDofs = commonValueForFlags;
			SubdomainMatrix = commonValueForFlags;
		}

		public bool IntersubdomainFreeDofs { get; set; }

		public IModifiedSubdomains ModifiedSubdomains { get; set; }

		public bool SubdomainFreeDofs { get; set; }

		public bool SubdomainMatrix { get; set; }

		public static ReanalysisOptions CreateWithAllDisabled()
			=> new ReanalysisOptions(false, new NullModifiedSubdomains());

		public static ReanalysisOptions CreateWithAllEnabled(IModifiedSubdomains modifiedSubdomains)
			=> new ReanalysisOptions(true, modifiedSubdomains);

	}
}
