-- First, create the song_likes table if it doesn't exist
CREATE TABLE IF NOT EXISTS song_likes (
    user_id INTEGER NOT NULL,
    song_metadata_id INTEGER NOT NULL,
    is_like BOOLEAN NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
    PRIMARY KEY (user_id, song_metadata_id)
);

-- Create the get_recommendations function with collaborative filtering
CREATE OR REPLACE FUNCTION get_recommendations(p_user_id INTEGER, p_limit INTEGER, p_exclude_songs INTEGER[])
RETURNS TABLE(song_id INTEGER, score DOUBLE PRECISION) AS $$
BEGIN
    RETURN QUERY
    WITH user_likes AS (
        -- Get songs the target user has liked
        SELECT song_metadata_id FROM song_likes 
        WHERE user_id = p_user_id AND is_like = true
    ),
    similar_users AS (
        -- Find users who liked the same songs (collaborative filtering)
        SELECT sl.user_id, COUNT(*) as shared_likes
        FROM song_likes sl
        INNER JOIN user_likes ul ON sl.song_metadata_id = ul.song_metadata_id
        WHERE sl.user_id != p_user_id AND sl.is_like = true
        GROUP BY sl.user_id
        ORDER BY shared_likes DESC
        LIMIT 50
    ),
    recommended AS (
        -- Get songs that similar users liked, excluding ones the user already rated
        SELECT 
            sl.song_metadata_id as song_id,
            SUM(su.shared_likes)::DOUBLE PRECISION as score
        FROM song_likes sl
        INNER JOIN similar_users su ON sl.user_id = su.user_id
        WHERE sl.is_like = true
          AND sl.song_metadata_id NOT IN (SELECT song_metadata_id FROM user_likes)
          AND (p_exclude_songs IS NULL OR sl.song_metadata_id != ALL(p_exclude_songs))
        GROUP BY sl.song_metadata_id
        ORDER BY score DESC
        LIMIT p_limit
    )
    SELECT r.song_id, r.score FROM recommended r;
END;
$$ LANGUAGE plpgsql;

-- 1. Enable the pgvector extension
CREATE EXTENSION IF NOT EXISTS vector;

-- 2. Add an embedding column to song_likes (or create a separate embeddings table)
ALTER TABLE song_likes ADD COLUMN IF NOT EXISTS embedding vector(384);

-- 3. Create an IVFFlat index for fast similarity search
CREATE INDEX IF NOT EXISTS song_likes_embedding_idx 
ON song_likes USING ivfflat (embedding vector_cosine_ops)
WITH (lists = 100);

-- 4. Update get_recommendations to use vector similarity
CREATE OR REPLACE FUNCTION get_recommendations(p_user_id INTEGER, p_limit INTEGER, p_exclude_songs INTEGER[])
RETURNS TABLE(song_id INTEGER, score DOUBLE PRECISION) AS $$
BEGIN
    RETURN QUERY
    WITH user_embedding AS (
        -- Average the embeddings of songs the user liked
        SELECT AVG(embedding) as avg_embedding
        FROM song_likes
        WHERE user_id = p_user_id AND is_like = true AND embedding IS NOT NULL
    )
    SELECT 
        sl.song_metadata_id as song_id,
        (1 - (sl.embedding <=> ue.avg_embedding))::DOUBLE PRECISION as score
    FROM song_likes sl, user_embedding ue
    WHERE sl.embedding IS NOT NULL
      AND sl.song_metadata_id NOT IN (
          SELECT song_metadata_id FROM song_likes WHERE user_id = p_user_id
      )
      AND (p_exclude_songs IS NULL OR sl.song_metadata_id != ALL(p_exclude_songs))
    ORDER BY sl.embedding <=> ue.avg_embedding
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql;