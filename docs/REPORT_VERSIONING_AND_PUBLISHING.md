# Report Versioning and Publishing

Reports are saved as versions with workflow states such as draft, published,
and archived. Users with the required report permissions can edit drafts,
review version history, and publish reports to a workspace folder.

## Drafts and versions

- Creating a report starts a draft.
- Saving changes updates the active draft; published versions are not
  overwritten.
- After publication, further edits are saved as a new draft/version.
- Drafts can be reopened from the report library.

## Publishing

Publishing a draft makes that version available to authorized Report Viewers
in the selected workspace folder. Default folders include **TVS**, **ECS**, and
**CHILLER**. Designers may create additional folders through the publish
workflow.

When a newer version is published, the previous published version is archived.
The API enforces publishing permission and the viewer role's access to the
current published version.

## Unpublishing and deletion

An authorized administrator or report owner can unpublish or delete a report.
Unpublishing removes the report from the viewer catalog and clears published
access. Deletion disables the report rather than removing source database
objects. A folder containing reports cannot be deleted until its reports are
removed or reassigned.

## Sharing and portability

Published report links are signed bearer capabilities; anyone with an
anonymous link may be able to access the report. Treat such links as secrets
and distribute them only to intended recipients. Link lifetime and access
requirements depend on the configured sharing workflow.

After upgrading to the new signing namespace, re-create previously issued
report-share and portable-package links; older signed URLs will no longer
validate.

Portable report packages can move report definitions between workspaces.
They do not contain saved data-source credentials; imported datasets must be
mapped to sources available in the destination workspace.
