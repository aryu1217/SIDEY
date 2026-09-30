import Foundation

extension AppCoordinator {
    func startDrawRefresh() {
        drawRefreshTask?.cancel()
        refreshDrawEvents()
        drawRefreshTask = Task { [weak self] in
            while !Task.isCancelled {
                do { try await Task.sleep(for: .seconds(30)) } catch { return }
                self?.refreshDrawEvents()
            }
        }
    }

    func refreshDrawEvents() {
        drawRefreshGeneration += 1
        let generation = drawRefreshGeneration
        // Apply the server's expiry timestamp even if the next poll cannot reach it.
        overlayWindows.refreshDrawIcons()
        guard let backend, let roomID = model.activeRoom?.id,
              let viewerID = model.currentUserID else {
            model.drawEvents = []
            model.drawEventsRoomID = nil
            overlayWindows.refreshDrawIcons()
            return
        }
        Task { [weak self] in
            guard let self else { return }
            do {
                let events = try await backend.listDrawEvents(roomID: roomID)
                guard generation == drawRefreshGeneration,
                      model.activeRoom?.id == roomID,
                      model.currentUserID == viewerID else { return }
                model.drawEvents = events
                model.drawEventsRoomID = roomID
                overlayWindows.refreshDrawIcons()
            } catch {
                // Keep the messenger usable when a background refresh fails.
                // Explicit draw actions report their errors to the user.
            }
        }
    }

    func createDrawEvent(kind: DrawKind, participants: [UUID], entryLabels: [String]?, ladderLabels: [String]?) {
        let entryCount = entryLabels?.count ?? participants.count
        guard !model.drawOperationInProgress,
              let backend,
              let room = model.activeRoom,
              (2...12).contains(entryCount),
              (entryLabels == nil || participants.isEmpty),
              Set(participants).count == participants.count,
              Set(participants).isSubset(of: Set(room.members.map(\.userID))),
              entryLabels?.allSatisfy({ validDrawLabel($0) }) ?? true,
              kind == .ladder ? ladderLabels?.count == entryCount : ladderLabels == nil,
              ladderLabels?.allSatisfy({ validDrawLabel($0) }) ?? true
        else { return }
        model.drawOperationInProgress = true
        Task { [weak self] in
            guard let self else { return }
            defer { model.drawOperationInProgress = false }
            do {
                _ = try await backend.createDrawEvent(
                    roomID: room.id,
                    kind: kind,
                    participantIDs: participants,
                    entryLabels: entryLabels,
                    ladderLabels: ladderLabels
                )
                refreshDrawEvents()
                model.presentSuccess(L10n.text("draw.created"))
            } catch {
                model.errorMessage = L10n.format("draw.create_failed", error.localizedDescription)
            }
        }
    }

    func openDrawEvent(_ eventID: UUID) {
        guard let backend, let room = model.activeRoom,
              model.drawEvents.contains(where: { $0.id == eventID }) else { return }
        drawResultWindowStorage?.close()
        drawOpenGeneration += 1
        let openGeneration = drawOpenGeneration
        Task { [weak self] in
            guard let self else { return }
            do {
                let event = try await backend.openDrawEvent(id: eventID)
                guard model.activeRoom?.id == room.id else { return }
                drawRefreshGeneration += 1
                model.drawEvents.removeAll { $0.id == event.id }
                model.drawEvents.append(event)
                overlayWindows.refreshDrawIcons()
                if openGeneration == drawOpenGeneration {
                    drawResultWindow.show(event: event, room: room, screenIdentifier: overlayWindows.currentScreenIdentifier)
                }
            } catch {
                model.errorMessage = L10n.format("draw.open_failed", error.localizedDescription)
            }
        }
    }

    func replayDrawEvent(_ eventID: UUID) {
        guard let room = model.activeRoom,
              let event = model.drawEvents.first(where: { $0.id == eventID }) else { return }
        drawResultWindow.show(event: event, room: room, screenIdentifier: overlayWindows.currentScreenIdentifier)
    }

    func closeDrawEvent(_ eventID: UUID) {
        guard let backend,
              let event = model.drawEvents.first(where: { $0.id == eventID }),
              event.creatorID == model.currentUserID else { return }
        Task { [weak self] in
            guard let self else { return }
            do {
                _ = try await backend.closeDrawEvent(id: eventID)
                refreshDrawEvents()
            } catch {
                model.errorMessage = L10n.format("draw.close_failed", error.localizedDescription)
            }
        }
    }

    func deleteDrawEvent(_ eventID: UUID) {
        guard !model.drawOperationInProgress,
              let backend,
              let roomID = model.activeRoom?.id,
              let event = model.drawEvents.first(where: { $0.id == eventID && $0.roomID == roomID }),
              event.creatorID == model.currentUserID else { return }
        model.drawOperationInProgress = true
        Task { [weak self] in
            guard let self else { return }
            defer { model.drawOperationInProgress = false }
            do {
                try await backend.deleteDrawEvent(id: eventID)
                guard model.activeRoom?.id == roomID else { return }
                drawRefreshGeneration += 1
                drawOpenGeneration += 1
                model.drawEvents.removeAll { $0.id == eventID }
                overlayWindows.refreshDrawIcons()
                refreshDrawEvents()
            } catch {
                model.errorMessage = L10n.format("draw.delete_failed", error.localizedDescription)
            }
        }
    }
}

private func validDrawLabel(_ raw: String) -> Bool {
    let label = raw.trimmingCharacters(in: .whitespacesAndNewlines)
    return (1...80).contains(label.count)
        && !label.contains(where: { "\r\n\t".contains($0) })
}
