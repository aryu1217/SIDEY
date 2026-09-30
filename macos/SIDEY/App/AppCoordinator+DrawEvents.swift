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
        refreshDrawHistory()
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

    func selectDrawHistoryRoom(_ roomID: UUID) {
        guard model.rooms.contains(where: { $0.id == roomID }) else { return }
        if model.drawHistoryRoomID != roomID {
            drawOpenGeneration += 1
            model.drawHistoryRoomID = roomID
            model.drawHistoryEvents = []
            model.drawHistoryError = nil
        }
        refreshDrawHistory()
    }

    func refreshDrawHistory() {
        drawHistoryRefreshGeneration += 1
        let generation = drawHistoryRefreshGeneration
        guard let backend, let viewerID = model.currentUserID,
              let roomID = model.drawHistoryRoomID,
              model.rooms.contains(where: { $0.id == roomID }) else {
            model.drawHistoryEvents = []
            model.drawHistoryLoading = false
            model.drawHistoryError = nil
            return
        }
        model.drawHistoryLoading = true
        model.drawHistoryError = nil
        Task { [weak self] in
            guard let self else { return }
            do {
                let events = try await backend.listDrawEvents(roomID: roomID)
                guard generation == drawHistoryRefreshGeneration,
                      model.currentUserID == viewerID,
                      model.drawHistoryRoomID == roomID,
                      model.rooms.contains(where: { $0.id == roomID }) else { return }
                model.drawHistoryEvents = events
                model.drawHistoryLoading = false
            } catch {
                guard generation == drawHistoryRefreshGeneration,
                      model.currentUserID == viewerID,
                      model.drawHistoryRoomID == roomID else { return }
                model.drawHistoryLoading = false
                model.drawHistoryError = L10n.text("draw.history.load_failed")
            }
        }
    }

    func openDrawEvent(_ eventID: UUID, fromHistory: Bool = false) {
        let events = fromHistory ? model.drawHistoryEvents : model.drawEvents
        guard let backend, let viewerID = model.currentUserID,
              let cached = events.first(where: { $0.id == eventID }),
              let room = model.rooms.first(where: { $0.id == cached.roomID }),
              fromHistory || model.activeRoom?.id == room.id else { return }
        drawResultWindowStorage?.close()
        drawOpenGeneration += 1
        let openGeneration = drawOpenGeneration
        Task { [weak self] in
            guard let self else { return }
            do {
                let event = try await backend.openDrawEvent(id: eventID)
                guard openGeneration == drawOpenGeneration,
                      model.currentUserID == viewerID,
                      model.rooms.contains(where: { $0.id == room.id }),
                      fromHistory ? model.drawHistoryRoomID == room.id : model.activeRoom?.id == room.id
                else { return }
                if model.drawEventsRoomID == room.id {
                    drawRefreshGeneration += 1
                    model.drawEvents.removeAll { $0.id == event.id }
                    model.drawEvents.append(event)
                    overlayWindows.refreshDrawIcons()
                }
                if model.drawHistoryRoomID == room.id {
                    drawHistoryRefreshGeneration += 1
                    model.drawHistoryLoading = false
                    model.drawHistoryEvents.removeAll { $0.id == event.id }
                    model.drawHistoryEvents.append(event)
                }
                drawResultWindow.show(event: event, room: room,
                                      screenIdentifier: overlayWindows.currentScreenIdentifier,
                                      followsActiveRoom: !fromHistory)
            } catch {
                guard openGeneration == drawOpenGeneration,
                      model.currentUserID == viewerID else { return }
                model.errorMessage = L10n.format("draw.open_failed", error.localizedDescription)
            }
        }
    }

    func replayDrawEvent(_ eventID: UUID) {
        openDrawEvent(eventID, fromHistory: true)
    }

    func closeDrawEvent(_ eventID: UUID) {
        guard let backend,
              let event = (model.drawHistoryEvents + model.drawEvents).first(where: { $0.id == eventID }),
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
              let viewerID = model.currentUserID,
              let event = (model.drawHistoryEvents + model.drawEvents).first(where: { $0.id == eventID }),
              model.rooms.contains(where: { $0.id == event.roomID }),
              event.creatorID == viewerID else { return }
        model.drawOperationInProgress = true
        Task { [weak self] in
            guard let self else { return }
            defer { model.drawOperationInProgress = false }
            do {
                try await backend.deleteDrawEvent(id: eventID)
                guard model.currentUserID == viewerID else { return }
                drawRefreshGeneration += 1
                drawHistoryRefreshGeneration += 1
                drawOpenGeneration += 1
                model.drawEvents.removeAll { $0.id == eventID }
                model.drawHistoryEvents.removeAll { $0.id == eventID }
                overlayWindows.refreshDrawIcons()
                refreshDrawEvents()
            } catch {
                model.errorMessage = L10n.format("draw.delete_failed", error.localizedDescription)
            }
        }
    }
}

private func validDrawLabel(_ raw: String) -> Bool {
    DrawEntryLimits.isValidLabel(raw)
}
