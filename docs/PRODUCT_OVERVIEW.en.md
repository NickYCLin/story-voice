# StoryVoice

> StoryVoice is an open-source, self-hosted AI audiobook studio. Import EPUB or TXT, organize characters and dialogue, cast voices, and review the script before generating an audiobook.

Last updated: 2026-09-07

EPUB / TXT import · Consistent series casts · Human review · MIT licensed

## For a short story or a whole series

### Authors and storytellers

Turn your own or licensed writing into audio. Audition the voices and adjust each character before producing the book.

### Series production teams

Reuse characters and voices across books, review dialogue chapter by chapter, and retain finished audio while preparing a new version.

### Self-hosters and developers

Manage a library in your own environment. Configure local models, speech services, or API integrations to fit your workflow.

## From manuscript to audiobook in four steps

1. **Import your story** — Upload an authorized, DRM-free EPUB or UTF-8 TXT file to organize chapters and source text.
2. **Cast the voices** — Check AI suggestions for characters and speakers. Assign voices to your narrator and cast.
3. **Review and audition** — Check dialogue assignments chapter by chapter. Preview individual lines with supported voices and correct uncertain results.
4. **Generate and listen** — Start a narration job, follow its progress, then play or download the finished MP3.

## Characters, scripts, and audio in one place

### Keep character voices consistent

Reuse character profiles and voices across a series. Supported speech services can also provide scene-specific voice variants.

### You decide who is speaking

AI helps identify narration, dialogue, and speakers. Correct assignments, accept suggestions, and approve the script before production.

### Follow progress and review new versions

Track, cancel, and retry narration jobs. Series rebuilds produce a candidate version before you confirm the switch to the new audio.

### Organize books and share reading

Order books into collections and share read-only access by another registered user's email. Private notes and narration audio are excluded.

## A few things to know before you start

- **Input and output**: Import EPUB or UTF-8 TXT; export audiobooks as MP3.
- **Language and voices**: Taiwan-Mandarin-first production, primarily for Traditional Chinese content. Available voices depend on deployment settings and speech services.
- **Hosting and cost**: MIT licensed and self-hosted. Budget separately for servers, GPUs, and any external speech services.
- **Content and data**: Use content you own or are authorized to transform. No DRM circumvention. External models or speech services receive the text or audio needed for their work.

## Questions before you begin

### Do I have to start with multiple characters?

No. Import a book and use a single voice available in your deployment. Add a series cast and a reviewed narration script when you want a multi-character production.

### Will AI identify every character and line correctly?

No guarantee. AI suggests characters and speakers; ambiguous or low-confidence passages still need human review. You can correct assignments before confirming the script.

### Can I give a character a custom voice?

The character voice studio supports creating and managing custom voices. Actual availability depends on configured speech services, samples, and authorization. Audition a voice before using it for a whole book.

### Does installation enable every AI feature?

Models and speech services still need configuration. Local LLMs, GPU speech, the public voice catalog, and developer APIs have separate availability requirements shown in the interface.

### Are my books and audio public?

Workspaces require sign-in and restrict books and generated audio by account. You can explicitly share read-only collections. Self-hosting does not mean every computation is local; data flow depends on the models and speech services you choose.

## Source and current status

- [GitHub](https://github.com/NickYCLin/story-voice)
- [Verified project status](https://github.com/NickYCLin/story-voice/blob/main/docs/PROJECT_STATUS.md)
- License: MIT
