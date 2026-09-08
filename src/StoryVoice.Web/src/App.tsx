import { lazy } from 'react'
import { Route, Routes } from 'react-router-dom'

import { AppLayout } from './AppLayout'
import { DeveloperDocsPage } from './pages/DeveloperDocsPage'
import { LandingPage } from './pages/LandingPage'
import { NotFoundPage } from './pages/NotFoundPage'
import { PublicVoicesPage } from './pages/PublicVoicesPage'

const CharacterLibraryPage = lazy(() => import('./pages/CharacterLibraryPage').then(module => ({ default: module.CharacterLibraryPage })))
const CollectionDetailPage = lazy(() => import('./pages/CollectionDetailPage').then(module => ({ default: module.CollectionDetailPage })))
const CollectionsPage = lazy(() => import('./pages/CollectionsPage').then(module => ({ default: module.CollectionsPage })))
const DeveloperConsolePage = lazy(() => import('./pages/DeveloperConsolePage').then(module => ({ default: module.DeveloperConsolePage })))
const DeveloperCredentialsPage = lazy(() => import('./pages/DeveloperCredentialsPage').then(module => ({ default: module.DeveloperCredentialsPage })))
const DeveloperPlaygroundPage = lazy(() => import('./pages/DeveloperPlaygroundPage').then(module => ({ default: module.DeveloperPlaygroundPage })))
const DeveloperProjectPage = lazy(() => import('./pages/DeveloperProjectPage').then(module => ({ default: module.DeveloperProjectPage })))
const DeveloperUsagePage = lazy(() => import('./pages/DeveloperUsagePage').then(module => ({ default: module.DeveloperUsagePage })))
const LibraryPage = lazy(() => import('./pages/LibraryPage').then(module => ({ default: module.LibraryPage })))
const SharedCollectionPage = lazy(() => import('./pages/SharedCollectionPage').then(module => ({ default: module.SharedCollectionPage })))
const SharedWithMePage = lazy(() => import('./pages/SharedWithMePage').then(module => ({ default: module.SharedWithMePage })))
const SeriesCastPanel = lazy(() => import('./SeriesCastPanel').then(module => ({ default: module.SeriesCastPanel })))

function App() {
  return (
    <Routes>
      <Route element={<LandingPage publicMode />} path="/about" />
      <Route element={<PublicVoicesPage />} path="/voices" />
      <Route element={<DeveloperDocsPage />} path="/developers/docs" />
      <Route element={<AppLayout />} path="/">
        <Route element={<LandingPage />} index />
        <Route element={<LibraryPage />} path="library" />
        <Route element={<LibraryPage />} path="library/:bookId" />
        <Route element={<CollectionsPage />} path="collections" />
        <Route element={<CollectionDetailPage />} path="collections/:collectionId" />
        <Route element={<SharedWithMePage />} path="shared" />
        <Route element={<CharacterLibraryPage />} path="characters" />
        <Route element={<DeveloperConsolePage />} path="developer" />
        <Route element={<DeveloperCredentialsPage />} path="developer/credentials" />
        <Route element={<DeveloperPlaygroundPage />} path="developer/playground" />
        <Route element={<DeveloperProjectPage />} path="developer/projects/:projectId" />
        <Route element={<DeveloperUsagePage />} path="developer/usage" />
        <Route element={<SeriesCastPanel />} path="/series" />
        <Route element={<SharedCollectionPage />} path="shared/:collectionId" />
      </Route>
      <Route element={<NotFoundPage />} path="*" />
    </Routes>
  )
}

export default App
