using Y4NGZCompany.Facility.Security;
namespace Y4NGZCompany.Facility.Stash
{
    public static class CompanyStashCodeDebugApi
    {
        public static string GetStatus()
        {
            return CctvSupportApi.GetStashCodeDebugReport();
        }
    }
}
