export type DeveloperProjectReference = {
  keyId: string
  projectId: string
}

export const canonicalDeveloperProjectReference = (
  project: DeveloperProjectReference,
) => project.projectId || project.keyId

export const findDeveloperProjectByReference = <T extends DeveloperProjectReference>(
  projects: readonly T[],
  reference: string,
) => projects.find((project) =>
  project.projectId === reference || project.keyId === reference)

export const normalizeDeveloperProjectReference = (
  projects: readonly DeveloperProjectReference[],
  reference: string,
) => {
  if (!reference) return ''
  const project = findDeveloperProjectByReference(projects, reference)
  return project ? canonicalDeveloperProjectReference(project) : ''
}
