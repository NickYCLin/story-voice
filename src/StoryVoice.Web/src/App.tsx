import { Route, Routes } from 'react-router-dom'

import { AppLayout } from './AppLayout'
import { CharacterLibraryPage } from './pages/CharacterLibraryPage'
import { CollectionDetailPage } from './pages/CollectionDetailPage'
import { CollectionsPage } from './pages/CollectionsPage'
import { DeveloperConsolePage } from './pages/DeveloperConsolePage'
import { DeveloperCredentialsPage } from './pages/DeveloperCredentialsPage'
import { DeveloperDocsPage } from './pages/DeveloperDocsPage'
import { DeveloperProjectPage } from './pages/DeveloperProjectPage'
import { LandingPage } from './pages/LandingPage'
import { LibraryPage } from './pages/LibraryPage'
import { PublicVoicesPage } from './pages/PublicVoicesPage'
import { SharedCollectionPage } from './pages/SharedCollectionPage'
import { SharedWithMePage } from './pages/SharedWithMePage'
import { SeriesCastPanel } from './SeriesCastPanel'

function App() {
  return (
    <Routes>
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
        <Route element={<DeveloperProjectPage />} path="developer/projects/:projectId" />
        <Route element={<SeriesCastPanel />} path="/series" />
        <Route element={<SharedCollectionPage />} path="shared/:collectionId" />
      </Route>
    </Routes>
  )
}

export default App
