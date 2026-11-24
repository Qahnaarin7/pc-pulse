package com.pcpulse.monitor;

import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

final class SavedComputers {
    private final SharedPreferences preferences;
    private final List<Computer> computers = new ArrayList<>();
    private Computer selected;

    SavedComputers(SharedPreferences preferences) {
        this.preferences = preferences;
        String activeId = preferences.getString("selected_computer", "");
        try {
            JSONArray saved = new JSONArray(preferences.getString("computers", "[]"));
            for (int index = 0; index < saved.length(); index++) {
                JSONObject item = saved.optJSONObject(index);
                if (item != null) {
                    String code = item.optString("code");
                    try {
                        PcConnection.fromCode(code);
                        Computer computer = new Computer(item.getString("id"), item.optString("name"), code);
                        computers.add(computer);
                        if (computer.id.equals(activeId)) {
                            selected = computer;
                        }
                    } catch (IllegalArgumentException | JSONException ignored) {
                        // Keep other valid saved PCs if a single profile is damaged.
                    }
                }
            }
        } catch (JSONException ignored) {
            // The legacy active pairing can still recover a profile.
        }
        if (selected == null && !computers.isEmpty()) {
            select(computers.get(0));
        } else if (selected == null) {
            String code = preferences.getString("pairing", "");
            try {
                PcConnection.fromCode(code);
                save(code, "", false);
            } catch (IllegalArgumentException ignored) {
                // A first launch has no pairing to migrate.
            }
        }
    }

    Computer selected() {
        return selected;
    }

    List<Computer> list() {
        return new ArrayList<>(computers);
    }

    void save(String code, String name, boolean updateSelected) {
        PcConnection.fromCode(code);
        Computer existing = null;
        for (Computer item : computers) {
            if (item.code.equals(code)) {
                existing = item;
                break;
            }
        }
        if (existing == null && updateSelected) {
            existing = selected;
        }
        Computer updated = new Computer(existing == null ? UUID.randomUUID().toString() : existing.id,
                name.isEmpty() && existing != null ? existing.name : name, code);
        if (existing == null) {
            computers.add(updated);
        } else {
            computers.set(computers.indexOf(existing), updated);
        }
        select(updated);
    }

    void select(Computer computer) {
        selected = computer;
        persist();
    }

    void forget(Computer computer) {
        computers.remove(computer);
        if (selected == computer) {
            selected = computers.isEmpty() ? null : computers.get(0);
        }
        persist();
    }

    void useDetectedName(String name) {
        if (selected != null && selected.name.isEmpty() && !name.isEmpty()) {
            Computer updated = new Computer(selected.id, name, selected.code);
            computers.set(computers.indexOf(selected), updated);
            select(updated);
        }
    }

    private void persist() {
        JSONArray saved = new JSONArray();
        try {
            for (Computer item : computers) {
                saved.put(new JSONObject().put("id", item.id).put("name", item.name).put("code", item.code));
            }
        } catch (JSONException impossible) {
            throw new IllegalStateException("Unable to encode a PC profile", impossible);
        }
        preferences.edit().putString("computers", saved.toString())
                .putString("selected_computer", selected == null ? "" : selected.id)
                .putString("pairing", selected == null ? "" : selected.code).apply();
    }

    static final class Computer {
        final String id;
        final String name;
        final String code;

        Computer(String id, String name, String code) {
            this.id = id;
            this.name = name;
            this.code = code;
        }
    }
}
