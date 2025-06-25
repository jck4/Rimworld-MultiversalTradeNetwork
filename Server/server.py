from fastapi import FastAPI, Depends, HTTPException, status, Header, Request
from pydantic import BaseModel
from typing import List, Dict, Any, Optional
import time
import uuid
from server.db import SessionLocal, init_db, ForSaleItem, SaleHistory, PendingSale, ActiveUser, ActiveToken, UserSession
import requests
import os
import jwt
from sqlalchemy.future import select
from sqlalchemy import delete, update
import asyncio
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from contextlib import asynccontextmanager
import logging

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger("MTN")

class LoginRequest(BaseModel):
    authTicket: str
    playerName: str = "Unknown"

class TradeRecordModel(BaseModel):
    DefName: str
    Quantity: int
    Price: int
    Quality: Optional[str] = ""

class SellRequest(BaseModel):
    records: List[TradeRecordModel]

class BuyItemRequest(BaseModel):
    def_name: str
    quantity: int = 1
    seller_name: str

class BuyRequest(BaseModel):
    items: List[BuyItemRequest]
    client_silver: int

class RemoveItemRequest(BaseModel):
    index: int

class TokenData(BaseModel):
    steam_id: str
    player_name: str

@asynccontextmanager
async def lifespan(app: FastAPI):
    await init_db()
    # Start cleanup task
    cleanup_task = asyncio.create_task(cleanup_expired_tokens())
    logger.info("Server started with database persistence")
    yield
    cleanup_task.cancel()

app = FastAPI(
    title="RimWorld Galactic Trade (MTN) API",
    description="API for facilitating cross-colony trade in RimWorld.",
    version="1.0.0",
    lifespan=lifespan
)

JWT_SECRET_KEY = os.environ.get("JWT_SECRET_KEY", "supersecret")
JWT_ALGORITHM = "HS256"
JWT_EXPIRATION_HOURS = 24

STEAM_API_KEY = os.environ.get("STEAM_API_KEY", "YOUR_STEAM_API_KEY")
APP_ID = 294100


def validate_steam_ticket_with_api(auth_ticket_base64: str):
    if not STEAM_API_KEY or STEAM_API_KEY == "YOUR_STEAM_API_KEY":
        logger.warning("Steam API key not configured, using mock validation for development")
        return "mock_steam_id_12345"
    
    url = "https://api.steampowered.com/ISteamUserAuth/AuthenticateUserTicket/v1/"
    params = {
        "key": STEAM_API_KEY,
        "appid": APP_ID,
        "ticket": auth_ticket_base64
    }
    
    # Retry logic for Steam API
    max_retries = 3
    for attempt in range(max_retries):
        try:
            response = requests.get(url, params=params, timeout=10)
            
            # If we get a 401 from Steam API, retry
            if response.status_code == 401:
                if attempt < max_retries - 1:
                    logger.warning(f"Steam API returned 401, retrying... (attempt {attempt + 1}/{max_retries})")
                    time.sleep(1)  # Wait 1 second before retry
                    continue
                else:
                    logger.error("Steam API returned 401 after all retries")
                    raise HTTPException(status_code=401, detail="Steam authentication failed")
            
            data = response.json()
            
            if "response" not in data:
                raise HTTPException(status_code=401, detail="Invalid Steam response")
            
            response_data = data["response"]
            
            if "error" in response_data:
                raise HTTPException(status_code=401, detail="Steam authentication failed")
            
            if "params" in response_data and "steamid" in response_data["params"]:
                steam_id = response_data["params"]["steamid"]
                logger.info(f"Steam ticket validated for Steam ID: {steam_id}")
                return steam_id
            else:
                raise HTTPException(status_code=401, detail="Steam authentication failed")
                
        except (requests.exceptions.RequestException, requests.exceptions.JSONDecodeError) as e:
            if attempt < max_retries - 1:
                logger.warning(f"Steam API request failed, retrying... (attempt {attempt + 1}/{max_retries}): {e}")
                time.sleep(1)
                continue
            else:
                logger.error(f"Steam API request failed after all retries: {e}")
                raise HTTPException(status_code=401, detail="Steam authentication failed")
        except Exception as e:
            logger.error(f"Steam validation error: {e}")
            raise HTTPException(status_code=401, detail="Steam authentication failed")
    
    # This should never be reached, but just in case
    raise HTTPException(status_code=401, detail="Steam authentication failed")

async def generate_jwt_token(steam_id: str, player_name: str, request: Request = None):
    expiration = time.time() + (JWT_EXPIRATION_HOURS * 3600)
    payload = {
        'steam_id': steam_id,
        'player_name': player_name,
        'exp': expiration,
        'iat': time.time(),
        'jti': str(uuid.uuid4())
    }
    token = jwt.encode(payload, JWT_SECRET_KEY, algorithm=JWT_ALGORITHM)
    
    async with SessionLocal() as session:
        db_token = ActiveToken(
            token=token,
            steam_id=steam_id,
            player_name=player_name,
            issued_at=time.time(),
            expires_at=expiration,
            revoked=0
        )
        session.add(db_token)
        
        existing_user = await session.execute(
            select(ActiveUser).where(ActiveUser.steam_id == steam_id)
        )
        existing_user = existing_user.scalar_one_or_none()
        
        if existing_user:
            existing_user.last_seen = time.time()
            existing_user.player_name = player_name
        else:
            db_user = ActiveUser(
                steam_id=steam_id,
                player_name=player_name,
                last_seen=time.time()
            )
            session.add(db_user)
        
        # Create new user session
        user_agent = request.headers.get("user-agent") if request else None
        
        db_session = UserSession(
            steam_id=steam_id,
            player_name=player_name,
            session_start=time.time(),
            last_activity=time.time(),
            is_active=True,
            user_agent=user_agent
        )
        session.add(db_session)
        
        await session.commit()
    
    return token

async def verify_jwt_token(token: str):
    try:
        async with SessionLocal() as session:
            # Check if token is revoked in database
            db_token = await session.execute(
                select(ActiveToken).where(ActiveToken.token == token)
            )
            db_token = db_token.scalar_one_or_none()
            
            if not db_token or db_token.revoked == 1:
                return None, "Token has been revoked"
            
            payload = jwt.decode(token, JWT_SECRET_KEY, algorithms=[JWT_ALGORITHM])
            steam_id = payload.get('steam_id')
            
            # Renew token expiration and update last_seen for active user
            if steam_id:
                new_expiration = time.time() + (JWT_EXPIRATION_HOURS * 3600)
                
                # Update token expiration in database
                await session.execute(
                    update(ActiveToken)
                    .where(ActiveToken.token == token)
                    .values(expires_at=new_expiration)
                )
                
                # Update last_seen for active user
                await session.execute(
                    update(ActiveUser)
                    .where(ActiveUser.steam_id == steam_id)
                    .values(last_seen=time.time())
                )
                
                await session.commit()
                
                # Update payload with new expiration
                payload['exp'] = new_expiration
            
            return payload, "Valid"
    except jwt.ExpiredSignatureError:
        return None, "Token has expired"
    except jwt.InvalidTokenError:
        return None, "Invalid token"

async def get_current_user(authorization: str = Header(None)) -> Dict[str, Any]:
    if not authorization:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Missing authorization header",
        )
    if not authorization.startswith('Bearer '):
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Invalid authorization format",
        )
    token = authorization[7:]
    payload, message = await verify_jwt_token(token)
    if not payload:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid token",
        )
    return payload

@app.post('/auth/login', tags=["Authentication"])
async def login(data: LoginRequest, request: Request):
    steam_id = validate_steam_ticket_with_api(data.authTicket)
    token = await generate_jwt_token(steam_id, data.playerName, request)
    return {
        "status": "success",
        "token": token,
        "token_type": "bearer",
        "player_name": data.playerName,
        "expires_in": JWT_EXPIRATION_HOURS * 3600
    }

@app.post('/auth/logout', tags=["Authentication"])
async def logout(authorization: str = Header(None), user: dict = Depends(get_current_user)):
    token = authorization[7:]
    steam_id = user.get('steam_id')
    
    async with SessionLocal() as session:
        # Revoke token in database
        await session.execute(
            update(ActiveToken)
            .where(ActiveToken.token == token)
            .values(revoked=1)
        )
        
        # End the current user session
        await session.execute(
            update(UserSession)
            .where(UserSession.steam_id == steam_id, UserSession.is_active == True)
            .values(session_end=time.time(), is_active=False)
        )
        
        # Check if user has any other active tokens
        active_tokens_count = await session.execute(
            select(ActiveToken)
            .where(ActiveToken.steam_id == steam_id, ActiveToken.revoked == 0)
        )
        active_tokens_count = active_tokens_count.scalars().all()
        
        # If no active tokens, remove user from active_users
        if len(active_tokens_count) == 0:
            await session.execute(
                delete(ActiveUser).where(ActiveUser.steam_id == steam_id)
            )
        
        await session.commit()
    
    logger.info(f"User logged out: {user.get('player_name')} (Steam ID: {steam_id})")
    return {"status": "success", "message": "Logged out successfully"}

@app.get('/auth/validate', tags=["Authentication"])
async def validate_token(user: dict = Depends(get_current_user)):
    return {
        "status": "success",
        "steam_id": user.get('steam_id'),
        "player_name": user.get('player_name'),
        "valid": True
    }

@app.post('/buy', tags=["Trading"])
async def handle_buy(data: BuyRequest, user: dict = Depends(get_current_user), request: Request = None):
    steam_id = user.get('steam_id')
    
    async with SessionLocal() as session:
        # Get all items for sale from database
        result = await session.execute(select(ForSaleItem))
        for_sale_items = result.scalars().all()
        
        # Validate all purchases first
        purchased_items, total_cost = [], 0
        
        for item_request in data.items:
            def_name = item_request.def_name
            quantity = item_request.quantity
            seller_name = item_request.seller_name
            
            # Find the specific item by def_name and seller_name
            matching_items = [item for item in for_sale_items 
                            if item.def_name == def_name and item.player_name == seller_name]
            
            if not matching_items:
                raise HTTPException(status_code=400, detail=f"Item {def_name} from {seller_name} is no longer available")
            
            # Use the first matching item (should be unique based on seller + item)
            item = matching_items[0]
            available_quantity = item.quantity
            
            if quantity > available_quantity:
                raise HTTPException(status_code=400, detail=f"Not enough {item.def_name} available. Requested: {quantity}, Available: {available_quantity}")
            
            item_cost = item.price * quantity
            total_cost += item_cost
        
        # Check if user has enough silver
        if hasattr(data, 'client_silver') and data.client_silver < total_cost:
            raise HTTPException(status_code=400, detail=f"Not enough silver. Required: {total_cost}, You have: {data.client_silver}")
        
        # Process purchases
        purchased_items, total_cost = [], 0
        
        for item_request in data.items:
            def_name = item_request.def_name
            quantity = item_request.quantity
            seller_name = item_request.seller_name
            
            # Find the specific item again (in case quantities changed)
            matching_items = [item for item in for_sale_items 
                            if item.def_name == def_name and item.player_name == seller_name]
            
            if not matching_items:
                continue  # Skip if item was already purchased by another user
            
            item = matching_items[0]
            available_quantity = item.quantity
            item_cost = item.price * quantity
            total_cost += item_cost
            
            # Create purchased item details
            purchased_item_details = {
                'DefName': item.def_name,
                'Quantity': quantity,
                'Price': item.price,
                'PlayerName': item.player_name,
                'Quality': item.quality,
                'seller_steam_id': item.seller_steam_id,
                'seller_name': item.player_name
            }
            
            if quantity == available_quantity:
                # Remove item completely from database
                await session.delete(item)
            else:
                # Update quantity in database
                item.quantity -= quantity
            
            purchased_items.append(purchased_item_details)
            
            # Create pending sale record
            if item.seller_steam_id:
                pending_sale = PendingSale(
                    seller_steam_id=item.seller_steam_id,
                    buyer_name=user.get('player_name'),
                    item=item.def_name,
                    quantity=quantity,
                    price=item.price,
                    total_silver=item_cost,
                    timestamp=time.time()
                )
                session.add(pending_sale)
        
        # Create sale history record
        from sqlalchemy import text
        import json
        await session.execute(text("PRAGMA foreign_keys=ON"))
        sale_record = SaleHistory(
            timestamp=time.time(),
            buyer_steam_id=steam_id,
            buyer_name=user.get('player_name'),
            purchased_items=json.dumps(purchased_items),
            total_cost=total_cost
        )
        session.add(sale_record)
        
        await session.commit()
    
    return {
        "status": "success",
        "purchased_items": purchased_items,
        "total_cost": int(total_cost)
    }

@app.post('/trade', tags=["Trading"])
async def handle_trade(data: SellRequest, user: dict = Depends(get_current_user), request: Request = None):
    newly_listed_items = []
    async with SessionLocal() as session:
        for record in data.records:
            item = record.model_dump()
            db_item = ForSaleItem(
                def_name=item['DefName'],
                quantity=item['Quantity'],
                price=item['Price'],
                player_name=user.get('player_name'),
                seller_steam_id=user.get('steam_id'),
                quality=item.get('Quality', ""),
                listed_at=time.time()
            )
            session.add(db_item)
            await session.commit()
            await session.refresh(db_item)
            newly_listed_items.append({
                'DefName': db_item.def_name,
                'Quantity': db_item.quantity,
                'Price': db_item.price,
                'PlayerName': db_item.player_name,
                'Quality': db_item.quality
            })
        from sqlalchemy import text
        import json
        await session.execute(text("PRAGMA foreign_keys=ON"))
        sale_record = SaleHistory(
            timestamp=time.time(),
            seller_steam_id=user.get('steam_id'),
            seller_name=user.get('player_name'),
            purchased_items=json.dumps(newly_listed_items),
            total_cost=0
        )
        session.add(sale_record)
        await session.commit()
    return {
        "status": "success",
        "received": len(data.records),
        "authenticated_user": user.get('player_name')
    }

@app.get('/forsale', tags=["Marketplace"])
async def get_for_sale(request: Request = None):
    async with SessionLocal() as session:
        result = await session.execute(select(ForSaleItem))
        items = result.scalars().all()
        filtered_items = []
        for item in items:
            filtered = {
                'DefName': str(item.def_name or ''),
                'Quantity': int(item.quantity or 0),
                'Price': int(item.price or 0),
                'PlayerName': str(item.player_name or ''),
                'Quality': str(item.quality or '')
            }
            filtered_items.append(filtered)
    return {"records": filtered_items}

@app.get('/my-items', tags=["User"])
async def get_my_items(user: dict = Depends(get_current_user)):
    user_steam_id = user.get('steam_id')
    async with SessionLocal() as session:
        result = await session.execute(select(ForSaleItem).where(ForSaleItem.seller_steam_id == user_steam_id))
        my_items = result.scalars().all()
        items = [{
            'DefName': item.def_name,
            'Quantity': item.quantity,
            'Price': item.price,
            'PlayerName': item.player_name,
            'Quality': item.quality
        } for item in my_items]
    return {"my_items": items, "count": len(items)}

@app.post('/remove-item', tags=["User"])
async def remove_item(data: RemoveItemRequest, user: dict = Depends(get_current_user)):
    item_index = data.index
    user_steam_id = user.get('steam_id')
    async with SessionLocal() as session:
        result = await session.execute(select(ForSaleItem).where(ForSaleItem.seller_steam_id == user_steam_id))
        my_items = result.scalars().all()
        if item_index >= len(my_items):
            raise HTTPException(status_code=400, detail="Invalid item index")
        item = my_items[item_index]
        await session.delete(item)
        await session.commit()
        logger.info(f"{user.get('player_name')} removed item: {item.def_name}")
        removed_item = {
            'DefName': item.def_name,
            'Quantity': item.quantity,
            'Price': item.price,
            'PlayerName': item.player_name,
            'Quality': item.quality
        }
    return {"status": "success", "removed_item": removed_item}

@app.get('/sales/pending', tags=["User"])
async def get_pending_sales(user: dict = Depends(get_current_user)):
    user_steam_id = user.get('steam_id')
    async with SessionLocal() as session:
        result = await session.execute(select(PendingSale).where(PendingSale.seller_steam_id == user_steam_id))
        user_pending_sales = result.scalars().all()
        sales_list = [{
            'buyer_name': sale.buyer_name,
            'item': sale.item,
            'quantity': sale.quantity,
            'price': sale.price,
            'total_silver': sale.total_silver,
            'timestamp': sale.timestamp
        } for sale in user_pending_sales]
    return {"pending_sales": sales_list, "count": len(sales_list)}

@app.post('/sales/claim', tags=["User"])
async def claim_sales(user: dict = Depends(get_current_user), data: Optional[Dict[str, Any]] = None):
    steam_id = user.get('steam_id')
    async with SessionLocal() as session:
        result = await session.execute(select(PendingSale).where(PendingSale.seller_steam_id == steam_id))
        user_pending_sales = result.scalars().all()
        if not user_pending_sales:
            # Return success response with 0 silver claimed instead of error
            return {
                "status": "success",
                "total_claimed": 0,
                "claimed_sales_count": 0,
                "message": "No pending sales to claim"
            }
        total_silver_claimed = sum(sale.price * sale.quantity for sale in user_pending_sales)
        for sale in user_pending_sales:
            await session.delete(sale)
        await session.commit()
    logger.info(f"{user.get('player_name')} claimed {total_silver_claimed} silver")
    return {
        "status": "success",
        "total_claimed": int(total_silver_claimed),
        "claimed_sales_count": len(user_pending_sales)
    }

@app.get('/user/info', tags=["User"])
async def get_user_info_endpoint(user: dict = Depends(get_current_user)):
    steam_id = user.get('steam_id')
    async with SessionLocal() as session:
        from sqlalchemy import text
        import json
        await session.execute(text("PRAGMA foreign_keys=ON"))
        result_items = await session.execute(select(ForSaleItem).where(ForSaleItem.seller_steam_id == steam_id))
        user_items = result_items.scalars().all()
        result_purchases = await session.execute(select(SaleHistory).where(SaleHistory.buyer_steam_id == steam_id))
        user_purchases = result_purchases.scalars().all()
        result_sales = await session.execute(select(SaleHistory).where(SaleHistory.seller_steam_id == steam_id))
        user_sales = result_sales.scalars().all()
        
        # Get user's last_seen from database
        user_info = await session.execute(
            select(ActiveUser).where(ActiveUser.steam_id == steam_id)
        )
        user_info = user_info.scalar_one_or_none()
        last_seen = user_info.last_seen if user_info else None
        
    return {
        "steam_id": steam_id,
        "player_name": user.get('player_name'),
        "items_for_sale": len(user_items),
        "total_purchases": len(user_purchases),
        "total_sales": len(user_sales),
        "last_seen": last_seen
    }

@app.get('/marketplace/stats', tags=["Marketplace"])
async def get_marketplace_stats():
    async with SessionLocal() as session:
        result_items = await session.execute(select(ForSaleItem))
        items = result_items.scalars().all()
        result_sales = await session.execute(select(SaleHistory))
        sales = result_sales.scalars().all()
        unique_sellers = {item.seller_steam_id for item in items if item.seller_steam_id}
        
        # Get active users count from sessions (last 30 minutes)
        current_time = time.time()
        active_threshold = current_time - (30 * 60)  # 30 minutes
        active_sessions = await session.execute(
            select(UserSession)
            .where(UserSession.last_activity > active_threshold, UserSession.is_active == True)
        )
        active_sessions = active_sessions.scalars().all()
        active_users_count = len(set(session.steam_id for session in active_sessions))
        
    return {
        "total_items_for_sale": len(items),
        "active_users": active_users_count,
        "total_transactions": len(sales),
        "unique_sellers": len(unique_sellers),
        "server_uptime": time.time(), # This should be improved to be a real uptime
        "active_users_definition": "Users with activity in the last 30 minutes"
    }

@app.get('/admin/users', tags=["Admin"])
async def get_active_users(user: dict = Depends(get_current_user)):
    async with SessionLocal() as session:
        # Get users with active sessions (logged in within last 30 minutes)
        current_time = time.time()
        active_threshold = current_time - (30 * 60)  # 30 minutes
        
        # Get users with recent activity
        active_sessions = await session.execute(
            select(UserSession)
            .where(UserSession.last_activity > active_threshold, UserSession.is_active == True)
        )
        active_sessions = active_sessions.scalars().all()
        
        # Get unique active users
        active_users_data = []
        seen_steam_ids = set()
        
        for session in active_sessions:
            if session.steam_id not in seen_steam_ids:
                seen_steam_ids.add(session.steam_id)
                
                # Get active token count for this user
                token_count = await session.execute(
                    select(ActiveToken)
                    .where(ActiveToken.steam_id == session.steam_id, ActiveToken.revoked == 0)
                )
                token_count = len(token_count.scalars().all())
                
                # Calculate session duration
                session_duration = current_time - session.session_start
                
                active_users_data.append({
                    "steam_id": session.steam_id,
                    "player_name": session.player_name,
                    "last_activity": session.last_activity,
                    "session_duration_minutes": int(session_duration / 60),
                    "active_tokens": token_count
                })
        
        return {
            "status": "success",
            "active_users": active_users_data,
            "total_users": len(active_users_data),
            "definition": "Users with activity in the last 30 minutes"
        }

@app.get('/admin/sessions', tags=["Admin"])
async def get_user_sessions(user: dict = Depends(get_current_user), limit: int = 50):
    """Get recent user sessions for monitoring"""
    async with SessionLocal() as session:
        result = await session.execute(
            select(UserSession)
            .order_by(UserSession.session_start.desc())
            .limit(limit)
        )
        sessions = result.scalars().all()
        
        sessions_data = []
        for session in sessions:
            session_duration = None
            if session.session_end:
                session_duration = int((session.session_end - session.session_start) / 60)
            else:
                session_duration = int((time.time() - session.session_start) / 60)
            
            sessions_data.append({
                "steam_id": session.steam_id,
                "player_name": session.player_name,
                "session_start": session.session_start,
                "session_end": session.session_end,
                "session_duration_minutes": session_duration,
                "is_active": session.is_active,
                "user_agent": session.user_agent
            })
        
        return {
            "status": "success",
            "sessions": sessions_data,
            "total_sessions": len(sessions_data)
        }

@app.get('/debug/pending_sales')
async def debug_pending_sales():
    async with SessionLocal() as session:
        result = await session.execute(select(PendingSale))
        all_pending = result.scalars().all()
        debug_list = [{
            'seller_steam_id': sale.seller_steam_id,
            'buyer_name': sale.buyer_name,
            'item': sale.item,
            'quantity': sale.quantity,
            'price': sale.price,
            'total_silver': sale.total_silver,
            'timestamp': sale.timestamp
        } for sale in all_pending]
    return debug_list

@app.get('/admin/cleanup', tags=["Admin"])
async def manual_cleanup(user: dict = Depends(get_current_user)):
    """Manual cleanup of expired tokens and inactive users"""
    try:
        current_time = time.time()
        async with SessionLocal() as session:
            # Remove expired tokens
            expired_tokens = await session.execute(
                delete(ActiveToken).where(ActiveToken.expires_at < current_time)
            )
            
            # Close inactive sessions
            inactive_session_threshold = current_time - (2 * 3600)  # 2 hours
            await session.execute(
                update(UserSession)
                .where(UserSession.last_activity < inactive_session_threshold, UserSession.is_active == True)
                .values(session_end=current_time, is_active=False)
            )
            
            # Remove users who haven't been seen in 24 hours
            inactive_threshold = current_time - (24 * 3600)
            inactive_users = await session.execute(
                select(ActiveUser).where(ActiveUser.last_seen < inactive_threshold)
            )
            inactive_users = inactive_users.scalars().all()
            
            for user_obj in inactive_users:
                # Remove all tokens for inactive users
                await session.execute(
                    delete(ActiveToken).where(ActiveToken.steam_id == user_obj.steam_id)
                )
                # Remove the user
                await session.delete(user_obj)
            
            await session.commit()
            
            return {
                "status": "success",
                "message": f"Cleanup completed. Removed {len(inactive_users)} inactive users and their tokens, closed inactive sessions.",
                "inactive_users_removed": len(inactive_users)
            }
            
    except Exception as e:
        logger.error(f"Manual cleanup error: {e}")
        raise HTTPException(status_code=500, detail=f"Cleanup failed: {str(e)}")

async def cleanup_expired_tokens():
    """Background task to clean up expired tokens and inactive users"""
    while True:
        try:
            current_time = time.time()
            async with SessionLocal() as session:
                # Remove expired tokens
                await session.execute(
                    delete(ActiveToken).where(ActiveToken.expires_at < current_time)
                )
                
                # Close sessions that have been inactive for more than 2 hours
                inactive_session_threshold = current_time - (2 * 3600)  # 2 hours
                await session.execute(
                    update(UserSession)
                    .where(UserSession.last_activity < inactive_session_threshold, UserSession.is_active == True)
                    .values(session_end=current_time, is_active=False)
                )
                
                # Remove users who haven't been seen in 24 hours
                inactive_threshold = current_time - (24 * 3600)
                inactive_users = await session.execute(
                    select(ActiveUser).where(ActiveUser.last_seen < inactive_threshold)
                )
                inactive_users = inactive_users.scalars().all()
                
                for user in inactive_users:
                    # Remove all tokens for inactive users
                    await session.execute(
                        delete(ActiveToken).where(ActiveToken.steam_id == user.steam_id)
                    )
                    # Remove the user
                    await session.delete(user)
                
                await session.commit()
                
                if inactive_users:
                    logger.info(f"Removed {len(inactive_users)} inactive users and their tokens")
                    
        except Exception as e:
            logger.error(f"Cleanup task error: {e}")
        
        # Run cleanup every hour
        await asyncio.sleep(3600)

@app.exception_handler(RequestValidationError)
async def validation_exception_handler(request: Request, exc: RequestValidationError):
    logger.error(f"Validation error: {exc}")
    logger.error(f"Request body: {await request.body()}")
    return JSONResponse(
        status_code=422,
        content={
            "detail": "Validation error",
            "errors": exc.errors(),
            "body": str(await request.body())
        }
    )

if __name__ == "__main__" or __package__:
    import asyncio
    asyncio.run(init_db())
    import uvicorn
    uvicorn.run(app, host='0.0.0.0', port=5000) 