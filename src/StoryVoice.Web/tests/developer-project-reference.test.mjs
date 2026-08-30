import assert from 'node:assert/strict'
import test from 'node:test'

import {
  canonicalDeveloperProjectReference,
  findDeveloperProjectByReference,
  normalizeDeveloperProjectReference,
} from '../src/developerProjectReference.ts'

const projects = [
  { keyId: 'key_legacy', projectId: 'project_canonical', displayName: 'Canonical project' },
  { keyId: 'key_fallback', projectId: '', displayName: 'Legacy project' },
]

test('developer project references accept keyId but expose canonical projectId', () => {
  assert.equal(findDeveloperProjectByReference(projects, 'key_legacy')?.displayName, 'Canonical project')
  assert.equal(findDeveloperProjectByReference(projects, 'project_canonical')?.displayName, 'Canonical project')
  assert.equal(normalizeDeveloperProjectReference(projects, 'key_legacy'), 'project_canonical')
  assert.equal(normalizeDeveloperProjectReference(projects, 'project_canonical'), 'project_canonical')
})

test('developer project references preserve legacy fallback and reject unknown values', () => {
  assert.equal(canonicalDeveloperProjectReference(projects[1]), 'key_fallback')
  assert.equal(normalizeDeveloperProjectReference(projects, 'key_fallback'), 'key_fallback')
  assert.equal(normalizeDeveloperProjectReference(projects, 'unknown'), '')
  assert.equal(normalizeDeveloperProjectReference(projects, ''), '')
})
