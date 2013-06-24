using System;
using System.Collections.Generic;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using EsccWebTeam.Cms;
using EsccWebTeam.Cms.Permissions;
using Microsoft.ApplicationBlocks.ExceptionManagement;
using Microsoft.ContentManagement.Publishing;
using Microsoft.ContentManagement.Publishing.Extensions.Placeholders;

namespace Escc.Cms.FindResourcesInWrongGallery
{
    /// <summary>
    /// When rearranging content in Resource Manager for web authors, it needs to be in the right gallery or they will not have access to save the page.
    /// This tool is a one-off check to ensure resources are in the right gallery.
    /// </summary>
    class Program
    {
        private static List<ResourceToMove> resourcesToMove = new List<ResourceToMove>();
        private static Dictionary<string, List<string>> resourcesUsedByGroups = new Dictionary<string, List<string>>();

        static void Main(string[] args)
        {
            try
            {
                var traverser = new CmsTraverser();
                traverser.TraversingPlaceholder += new CmsEventHandler(traverser_TraversingPlaceholder);
                traverser.TraverseSite(PublishingMode.Unpublished, false);

                var body = new StringBuilder();
                foreach (ResourceToMove resource in resourcesToMove)
                {
                    body.Append("<p>Currently in: ").Append(resource.CurrentPath).Append("<br />Belongs in: ").Append(resource.MoveToFolder);
                    if (resourcesUsedByGroups.ContainsKey(resource.Guid))
                    {
                        foreach (string cmsGroup in resourcesUsedByGroups[resource.Guid])
                        {
                            body.Append("<br />" + cmsGroup);
                        }
                    }
                    body.Append("</p>");
                }

                using (var mail = new MailMessage(Environment.MachineName + "@eastsussex.gov.uk", "richard.mason@eastsussex.gov.uk"))
                {
                    mail.IsBodyHtml = true;
                    mail.Subject = "CMS resources to move";
                    mail.Body = body.ToString();

                    var smtp = new SmtpClient();
                    smtp.Send(mail);
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.Publish(ex);
                throw;
            }
        }

        private static void traverser_TraversingPlaceholder(object sender, CmsEventArgs e)
        {
            Console.WriteLine(e.Posting.UrlModePublished + ": " + e.Placeholder.Name);

            var image = e.Placeholder as ImagePlaceholder;
            if (image != null)
            {
                var resource = CmsUtilities.ParseResourceUrl(image.Src, e.Context);
                var cmsGroups = CmsPermissions.ReadCmsGroupsForChannel(e.Channel);
                if (cmsGroups[CmsRole.Editor].Count == 0) return;

                IsResourceInTheRightFolder(e, resource, cmsGroups);
            }
            else
            {
                var resourceLinks = Regex.Matches(e.Placeholder.Datasource.RawContent, CmsUtilities.DownloadLinkPattern, RegexOptions.IgnoreCase);
                if (resourceLinks.Count == 0) return;

                var cmsGroups = CmsPermissions.ReadCmsGroupsForChannel(e.Channel);
                if (cmsGroups[CmsRole.Editor].Count == 0) return;

                foreach (Match match in resourceLinks)
                {
                    var resource = CmsUtilities.ParseResourceUrl(match.Groups["url"].Value, e.Context);
                    IsResourceInTheRightFolder(e, resource, cmsGroups);
                }
            }
        }

        private static bool IsResourceInTheRightFolder(CmsEventArgs e, Resource resource, Dictionary<CmsRole, IList<string>> cmsGroups)
        {
            if (resource != null && resource.Parent != null && resource.Parent.Parent != null)
            {
                // Get the resource gallery which should match the name of the CMS group
                var gallery = resource.Parent;
                while (gallery.Parent != null && gallery.Parent.Parent != null && gallery.Parent.Parent != e.Context.RootResourceGallery)
                {
                    gallery = gallery.Parent;
                }

                // Is the resource in a folder named after the group?
                foreach (var cmsGroup in cmsGroups[CmsRole.Editor])
                {
                    if (cmsGroup.ToUpperInvariant() == gallery.Name.ToUpperInvariant())
                    {
                        // It's a match, so no problem here. But save details in case resource used across two groups.
                        if (!resourcesUsedByGroups.ContainsKey(resource.Guid))
                        {
                            resourcesUsedByGroups.Add(resource.Guid, new List<string>());
                        }
                        if (!resourcesUsedByGroups[resource.Guid].Contains(cmsGroup))
                        {
                            resourcesUsedByGroups[resource.Guid].Add(cmsGroup);
                        }
                        return true;
                    }
                }

                // Getting here means we have a resource and a channel with a web author, but no matching name
                resourcesToMove.Add(new ResourceToMove()
                    {
                        Guid = resource.Guid,
                        CurrentPath = resource.Path,
                        MoveToFolder = cmsGroups[CmsRole.Editor][0]
                    });
                return false;
            }
            return true;
        }

        private class ResourceToMove
        {
            public string Guid { get; set; }
            public string CurrentPath { get; set; }
            public string MoveToFolder { get; set; }
        }
    }
}
